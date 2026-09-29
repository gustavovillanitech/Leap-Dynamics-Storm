using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.DealLines.InventoryManagement
{
	/// <summary>
	/// Custom API: new_DistributeIncrementalRevenue (bound to new_deals)
	///
	/// Spreads the deal's Incremental Contract Value across the deal lines flagged
	/// Include in Auto-Proration, in proportion to what each line is worth. This is the rule
	/// Storm asked for: a line that is 10% of the deal takes 10% of the incremental revenue.
	///
	/// Two ways to write the result, decided by the deal's Incremental Pricing Method:
	///
	///  - Per Game Rate      -> writes Incremental Rate per Home/Away Game on each line. The
	///                          deal-level rate is what gets split, so the per-game story stays
	///                          intact and the line's revenue is still rate x incremental games.
	///  - Itemized / Flat    -> writes Incremental Revenue on each line directly, because there
	///                          is no per-game rate for the amount to come from.
	///
	/// Either way the parts add up to the contract value exactly: the rounding remainder is
	/// given to the largest line rather than left to drift.
	///
	/// The whole allocation is recalculated on every run: lines marked Include in Auto-Proration
	/// share the contract value, and lines that are NOT marked are cleared. Without that second
	/// half, unticking a line would leave its old amount stranded on the deal and the Unallocated
	/// Variance would go negative.
	///
	/// Runs as one transaction. If any line fails, nothing is written.
	///
	/// Registered as a Global (unbound) Custom API, so the deal comes in as a parameter rather
	/// than as a bound Target. That also lets it be called from a console or a background job,
	/// where there is no record context to bind to.
	///
	/// Request:   DealId (String, required), DryRun (Boolean, optional - report without writing)
	/// Response:  LinesUpdated (Integer), AmountDistributed (Decimal), Message (String)
	/// </summary>
	public class DistributeIncrementalRevenue : IPlugin
	{
		private const int CLAUSE_IN = 100000000;
		private const int CLAUSE_OPT_IN = 100000001;
		private const int METHOD_PER_GAME_RATE = 100000000;

		public void Execute(IServiceProvider serviceProvider)
		{
			var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
			var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = factory.CreateOrganizationService(context.UserId);

			bool dryRun = context.InputParameters.Contains("DryRun") && (bool)context.InputParameters["DryRun"];

			Guid dealId = GetDealId(context);
			tracing.Trace($"DistributeIncrementalRevenue on deal {dealId}, DryRun={dryRun}");

			Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet(
				"new_incrementalgamesclause",
				"new_incrementalpricingmethod",
				"new_awaygamebenefits",
				"new_incrementalhomegames",
				"new_incrementalawaygames",
				"new_investmentperhomegame",
				"new_investmentperawaygame",
				"new_incrementalcontractvalue"));

			OptionSetValue clause = deal.GetAttributeValue<OptionSetValue>("new_incrementalgamesclause");
			if (clause == null || (clause.Value != CLAUSE_IN && clause.Value != CLAUSE_OPT_IN))
			{
				throw new InvalidPluginExecutionException(
					"This deal has no incremental games clause to distribute. Set the Incremental Games Clause to In or Opt-In first.");
			}

			decimal contractValue = GetMoney(deal, "new_incrementalcontractvalue");
			if (contractValue <= 0m)
			{
				throw new InvalidPluginExecutionException(
					"Incremental Contract Value is empty, so there is nothing to distribute. Enter the per-game investment, or type the agreed amount, and try again.");
			}

			List<Entity> lines, excluded;
			GetDealLines(dealId, service, tracing, out lines, out excluded);

			if (lines.Count == 0)
			{
				throw new InvalidPluginExecutionException(
					"No deal lines are marked Include in Auto-Proration, so there is nowhere to put the incremental revenue. " +
					"Switch the grid to the Deal Lines - Incremental Games view and set that flag on the lines that carry the extra games.");
			}

			// Each line's weight is its base value - what it is worth before any incremental revenue.
			// Using the plain Total would let a second run compound on top of the first.
			decimal[] weights = lines.Select(l => Math.Max(0m, GetMoney(l, "new_total") - GetMoney(l, "new_incrementalrevenue"))).ToArray();
			decimal weightSum = weights.Sum();

			if (weightSum <= 0m)
			{
				// Every eligible line is worth zero. Proportional makes no sense, so split evenly
				// rather than refusing - the user asked for the amount to be placed somewhere.
				tracing.Trace("All eligible lines have a base value of zero; falling back to an even split.");
				for (int i = 0; i < weights.Length; i++) weights[i] = 1m;
				weightSum = weights.Length;
			}

			int largest = IndexOfLargest(weights);

			OptionSetValue method = deal.GetAttributeValue<OptionSetValue>("new_incrementalpricingmethod");
			bool perGameRate = method != null && method.Value == METHOD_PER_GAME_RATE;

			List<Entity> updates = perGameRate
				? BuildRateUpdates(deal, lines, weights, weightSum, largest, tracing)
				: BuildAmountUpdates(contractValue, lines, weights, weightSum, largest, tracing);

			// Lines that are no longer marked must give back whatever they were holding, or the
			// deal ends up allocating more than the contract is worth.
			List<Entity> clears = BuildClearUpdates(excluded, tracing);
			updates.AddRange(clears);

			if (!dryRun)
			{
				foreach (Entity update in updates)
					service.Update(update);
			}

			string clearedNote = clears.Count > 0
				? $" {clears.Count} line(s) not marked for auto-proration were cleared."
				: string.Empty;

			string message = dryRun
				? $"Dry run: {contractValue:C} would be shared across {lines.Count} line(s).{clearedNote} Nothing was written."
				: $"{contractValue:C} distributed across {lines.Count} line(s).{clearedNote}";

			context.OutputParameters["LinesUpdated"] = updates.Count;
			context.OutputParameters["AmountDistributed"] = contractValue;
			context.OutputParameters["Message"] = message;

			tracing.Trace(message);
		}

		/// <summary>
		/// Splits the deal-level per-game investment across the lines. Each line ends up with its
		/// own Incremental Rate per Home/Away Game, and those rates add up to the deal's rates, so
		/// the revenue the pre-operation handler calculates from them adds up to the contract value.
		/// </summary>
		private List<Entity> BuildRateUpdates(Entity deal, List<Entity> lines, decimal[] weights,
			decimal weightSum, int largest, ITracingService tracing)
		{
			decimal homePool = GetMoney(deal, "new_investmentperhomegame");
			bool awayBenefits = deal.GetAttributeValue<bool>("new_awaygamebenefits");
			decimal awayPool = awayBenefits ? GetMoney(deal, "new_investmentperawaygame") : 0m;

			decimal[] homeRates = Split(homePool, weights, weightSum, largest);
			decimal[] awayRates = Split(awayPool, weights, weightSum, largest);

			var updates = new List<Entity>();
			for (int i = 0; i < lines.Count; i++)
			{
				Entity update = new Entity("new_deallines", lines[i].Id);
				update["new_incrementalrateperhomegame"] = new Money(homeRates[i]);
				if (awayBenefits)
					update["new_incrementalrateperawaygame"] = new Money(awayRates[i]);

				updates.Add(update);
				tracing.Trace($"Line {lines[i].Id}: home rate {homeRates[i]}, away rate {(awayBenefits ? awayRates[i] : 0m)}");
			}
			return updates;
		}

		/// <summary>
		/// Writes each line's share of the contract value straight into Incremental Revenue. Used
		/// when the pricing method is Itemized by Line or Flat Amount, where the amount was agreed
		/// as a figure rather than derived from a rate.
		/// </summary>
		private List<Entity> BuildAmountUpdates(decimal contractValue, List<Entity> lines, decimal[] weights,
			decimal weightSum, int largest, ITracingService tracing)
		{
			decimal[] amounts = Split(contractValue, weights, weightSum, largest);

			var updates = new List<Entity>();
			for (int i = 0; i < lines.Count; i++)
			{
				Entity update = new Entity("new_deallines", lines[i].Id);
				update["new_incrementalrevenue"] = new Money(amounts[i]);
				updates.Add(update);
				tracing.Trace($"Line {lines[i].Id}: incremental revenue {amounts[i]}");
			}
			return updates;
		}

		/// <summary>
		/// Splits an amount by weight, rounded to cents, with the rounding remainder given to the
		/// largest line so the parts add back to the whole exactly.
		/// </summary>
		private decimal[] Split(decimal amount, decimal[] weights, decimal weightSum, int largest)
		{
			var parts = new decimal[weights.Length];
			if (amount == 0m) return parts;

			decimal running = 0m;
			for (int i = 0; i < weights.Length; i++)
			{
				parts[i] = Math.Round(amount * (weights[i] / weightSum), 2, MidpointRounding.AwayFromZero);
				running += parts[i];
			}

			parts[largest] += amount - running;
			return parts;
		}

		/// <summary>
		/// Every active line on the deal, split into the ones marked Include in Auto-Proration and
		/// the ones that are not. Both halves matter: the first shares the contract value, the
		/// second has to be cleared.
		/// </summary>
		private void GetDealLines(Guid dealId, IOrganizationService service, ITracingService tracing,
			out List<Entity> eligible, out List<Entity> excluded)
		{
			QueryExpression query = new QueryExpression("new_deallines")
			{
				ColumnSet = new ColumnSet(
					"new_total",
					"new_incrementalrevenue",
					"new_incrementalrateperhomegame",
					"new_incrementalrateperawaygame",
					"new_includeinautoproration",
					"new_name")
			};
			query.Criteria.AddCondition("new_dealid", ConditionOperator.Equal, dealId);
			query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

			List<Entity> all = service.RetrieveMultiple(query).Entities.ToList();

			eligible = all.Where(l => l.GetAttributeValue<bool>("new_includeinautoproration")).ToList();
			excluded = all.Where(l => !l.GetAttributeValue<bool>("new_includeinautoproration")).ToList();

			tracing.Trace($"{eligible.Count} eligible line(s), {excluded.Count} excluded line(s).");
		}

		/// <summary>
		/// Zeroes the incremental rates and amount on the lines that are not marked for
		/// auto-proration, but only on the ones actually carrying something - so the update count
		/// reflects real changes and untouched lines are not written for nothing.
		/// </summary>
		private List<Entity> BuildClearUpdates(List<Entity> excluded, ITracingService tracing)
		{
			var updates = new List<Entity>();

			foreach (Entity line in excluded)
			{
				bool carriesSomething =
					GetMoney(line, "new_incrementalrateperhomegame") != 0m ||
					GetMoney(line, "new_incrementalrateperawaygame") != 0m ||
					GetMoney(line, "new_incrementalrevenue") != 0m;

				if (!carriesSomething) continue;

				Entity update = new Entity("new_deallines", line.Id);
				update["new_incrementalrateperhomegame"] = new Money(0m);
				update["new_incrementalrateperawaygame"] = new Money(0m);
				update["new_incrementalrevenue"] = new Money(0m);

				updates.Add(update);
				tracing.Trace($"Line {line.Id} cleared: not marked for auto-proration.");
			}

			return updates;
		}

		private Guid GetDealId(IPluginExecutionContext context)
		{
			if (context.InputParameters.Contains("DealId"))
			{
				string raw = context.InputParameters["DealId"] as string;
				Guid parsed;
				if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw.Replace("{", "").Replace("}", ""), out parsed))
					return parsed;

				throw new InvalidPluginExecutionException(
					$"DealId is not a valid record id: '{raw}'.");
			}

			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is EntityReference targetRef)
				return targetRef.Id;

			if (context.PrimaryEntityId != Guid.Empty)
				return context.PrimaryEntityId;

			throw new InvalidPluginExecutionException(
				"DistributeIncrementalRevenue could not tell which deal to work on. Pass the deal's id in the DealId parameter.");
		}

		private int IndexOfLargest(decimal[] values)
		{
			int index = 0;
			for (int i = 1; i < values.Length; i++)
				if (values[i] > values[index]) index = i;
			return index;
		}

		private decimal GetMoney(Entity entity, string attributeName)
		{
			Money money = entity.GetAttributeValue<Money>(attributeName);
			return money != null ? money.Value : 0m;
		}
	}
}
