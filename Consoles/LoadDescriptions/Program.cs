using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LoadDescriptions
{
    /// <summary>
    /// Loads External + Internal descriptions from the rate card into Dynamics.
    ///
    /// Source: descriptions_load.csv (Name, Collection, ExternalDescription = rate-card col H,
    /// InternalDescription = rate-card col Q), pre-extracted from "2026 Rate Card ... V2".
    ///
    /// Targets:
    ///   External (H) -> product.new_descriptionorspecifications  (maps to Trak "specifications")
    ///                -> mirrored onto inventory.new_description
    ///   Internal (Q) -> product.new_internaldescription          (Dynamics-only, NOT synced to Trak)
    ///                -> mirrored onto inventory.new_internaldescription
    ///
    /// Match: product by Name (Collection used to disambiguate duplicate names).
    /// Delta-only: writes a field only when the source is non-empty AND differs from the current value
    /// (never blanks existing data). DryRun default. CSV backup before any write.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Sandbox: https://org00bff505.crm.dynamics.com/
        // Prod:    https://stormbasketball.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        // DryRun = true  -> preview + report only (no writes).
        // DryRun = false -> apply (asks for a typed "YES", backs up first).
        private const bool DryRun = false;

        // Path to the pre-extracted CSV. Defaults to the same folder as the .exe.
        private static string CsvPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "descriptions_load.csv");

        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";
        // ======================================================================

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string _stamp;
        private static string _outDir;

        private static void Main(string[] args)
        {
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _outDir = AppDomain.CurrentDomain.BaseDirectory;

            Console.ForegroundColor = (IsProd || !DryRun) ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  LoadDescriptions - external/internal descriptions load");
            Console.WriteLine($"  Target : {EnvUrl}  [{(IsProd ? "PROD" : "SANDBOX")}]");
            Console.WriteLine($"  Mode   : {(DryRun ? "DRY RUN (no writes)" : "APPLY (will write)")}");
            Console.WriteLine($"  CSV    : {CsvPath}");
            Console.WriteLine("=========================================================");
            Console.ResetColor();

            if (!File.Exists(CsvPath)) { Bye("CSV not found next to the .exe. Copy descriptions_load.csv there."); return; }

            List<Src> src = ReadCsv(CsvPath);
            Console.WriteLine($"CSV rows: {src.Count}");
            Console.WriteLine("Type 'Y' to continue:");
            if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "Y") { Bye("Cancelled."); return; }

            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
            Console.WriteLine("\nConnecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful!\n");

            using (service)
            {
                // Load products
                var products = RetrieveAll(service, new QueryExpression("new_product")
                { ColumnSet = new ColumnSet("new_name", "new_collection", "new_descriptionorspecifications", "new_internaldescription") });
                var byName = new Dictionary<string, List<Entity>>();
                foreach (Entity p in products)
                {
                    string k = Norm(GetString(p, "new_name"));
                    if (k.Length == 0) continue;
                    if (!byName.ContainsKey(k)) byName[k] = new List<Entity>();
                    byName[k].Add(p);
                }

                // Load inventory grouped by product
                var invByProduct = new Dictionary<Guid, List<Entity>>();
                foreach (Entity inv in RetrieveAll(service, new QueryExpression("new_inventory")
                { ColumnSet = new ColumnSet("new_productid", "new_description", "new_internaldescription") }))
                {
                    Guid pid = GetLookupId(inv, "new_productid");
                    if (pid == Guid.Empty) continue;
                    if (!invByProduct.ContainsKey(pid)) invByProduct[pid] = new List<Entity>();
                    invByProduct[pid].Add(inv);
                }
                Console.WriteLine($"Loaded {products.Count} products, {invByProduct.Values.Sum(x => x.Count)} inventory rows.\n");

                var collNameCache = new Dictionary<Guid, string>();
                var plan = new List<Plan>();
                var notFound = new List<Src>();
                var ambiguous = new List<Src>();

                foreach (Src s in src)
                {
                    if (!byName.TryGetValue(Norm(s.Name), out List<Entity> cands)) { notFound.Add(s); continue; }

                    Entity prod = null;
                    if (cands.Count == 1) prod = cands[0];
                    else
                    {
                        // disambiguate by Collection name
                        var matches = cands.Where(c => string.Equals(
                            ResolveCollName(service, c, collNameCache), s.Collection, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 1) prod = matches[0];
                        else { ambiguous.Add(s); continue; }
                    }

                    var pl = new Plan { Product = prod, Src = s };
                    // product deltas (only non-empty source that differs)
                    if (s.External.Length > 0 && GetString(prod, "new_descriptionorspecifications").Trim() != s.External)
                        pl.SetProdExternal = true;
                    if (s.Internal.Length > 0 && GetString(prod, "new_internaldescription").Trim() != s.Internal)
                        pl.SetProdInternal = true;
                    // inventory deltas
                    if (invByProduct.TryGetValue(prod.Id, out List<Entity> invs))
                        foreach (Entity inv in invs)
                        {
                            bool ext = s.External.Length > 0 && GetString(inv, "new_description").Trim() != s.External;
                            bool intr = s.Internal.Length > 0 && GetString(inv, "new_internaldescription").Trim() != s.Internal;
                            if (ext || intr) pl.InvUpdates.Add(new InvPlan { Inv = inv, SetExternal = ext, SetInternal = intr });
                        }
                    if (pl.SetProdExternal || pl.SetProdInternal || pl.InvUpdates.Count > 0) plan.Add(pl);
                }

                // Report
                string report = Path.Combine(_outDir, $"LoadDescriptions_Report_{(IsProd ? "PROD" : "SANDBOX")}_{_stamp}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("ProductName,ProductId,SetProdExternal,SetProdInternal,InventoryRowsToUpdate");
                foreach (Plan p in plan)
                    sb.AppendLine(string.Join(",", Csv(GetString(p.Product, "new_name")), p.Product.Id.ToString(),
                        p.SetProdExternal ? "Y" : "", p.SetProdInternal ? "Y" : "", p.InvUpdates.Count.ToString()));
                if (notFound.Count > 0) { sb.AppendLine(); sb.AppendLine("NOT FOUND (no product with this name):"); foreach (Src s in notFound) sb.AppendLine(Csv(s.Name) + "," + Csv(s.Collection)); }
                if (ambiguous.Count > 0) { sb.AppendLine(); sb.AppendLine("AMBIGUOUS (multiple products, collection did not disambiguate):"); foreach (Src s in ambiguous) sb.AppendLine(Csv(s.Name) + "," + Csv(s.Collection)); }
                File.WriteAllText(report, sb.ToString(), Utf8NoBom);

                int invTotal = plan.Sum(p => p.InvUpdates.Count);
                Console.WriteLine("==================== PLAN SUMMARY ====================");
                Console.WriteLine($"CSV rows                    : {src.Count}");
                Console.WriteLine($"Products to update          : {plan.Count(p => p.SetProdExternal || p.SetProdInternal)}");
                Console.WriteLine($"Inventory rows to update    : {invTotal}");
                Console.WriteLine($"Not found (name)            : {notFound.Count}");
                Console.WriteLine($"Ambiguous (name+collection) : {ambiguous.Count}");
                Console.WriteLine($"Report                      : {report}");
                Console.WriteLine("=====================================================\n");

                if (DryRun) { Bye("DryRun = true. No changes made. Review the report."); return; }
                if (plan.Count == 0) { Bye("Nothing to update."); return; }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"About to update {plan.Count} products and {invTotal} inventory rows in {EnvUrl}.");
                Console.ResetColor();
                Console.WriteLine("Type 'YES' to proceed:");
                if ((Console.ReadLine() ?? "") != "YES") { Bye("Cancelled."); return; }

                // Backup
                string backup = Path.Combine(_outDir, $"LoadDescriptions_Backup_{(IsProd ? "PROD" : "SANDBOX")}_{_stamp}.csv");
                var bk = new StringBuilder();
                bk.AppendLine("Type,RecordId,Name,ExternalBefore,InternalBefore");
                foreach (Plan p in plan)
                {
                    bk.AppendLine(string.Join(",", "Product", p.Product.Id, Csv(GetString(p.Product, "new_name")),
                        Csv(GetString(p.Product, "new_descriptionorspecifications")), Csv(GetString(p.Product, "new_internaldescription"))));
                    foreach (InvPlan ip in p.InvUpdates)
                        bk.AppendLine(string.Join(",", "Inventory", ip.Inv.Id, "",
                            Csv(GetString(ip.Inv, "new_description")), Csv(GetString(ip.Inv, "new_internaldescription"))));
                }
                File.WriteAllText(backup, bk.ToString(), Utf8NoBom);
                Console.WriteLine($"Backup written: {backup}\n");

                int okP = 0, okI = 0, err = 0;
                foreach (Plan p in plan)
                {
                    try
                    {
                        if (p.SetProdExternal || p.SetProdInternal)
                        {
                            var u = new Entity("new_product", p.Product.Id);
                            if (p.SetProdExternal) u["new_descriptionorspecifications"] = p.Src.External;
                            if (p.SetProdInternal) u["new_internaldescription"] = p.Src.Internal;
                            service.Update(u); okP++;
                        }
                        foreach (InvPlan ip in p.InvUpdates)
                        {
                            var u = new Entity("new_inventory", ip.Inv.Id);
                            if (ip.SetExternal) u["new_description"] = p.Src.External;
                            if (ip.SetInternal) u["new_internaldescription"] = p.Src.Internal;
                            service.Update(u); okI++;
                        }
                    }
                    catch (Exception ex) { err++; Err($"{GetString(p.Product, "new_name")}: {ex.Message}"); }
                }
                Console.WriteLine($"\nDone. Products updated: {okP}, Inventory updated: {okI}, errors: {err}.");
                Bye("Finished.");
            }
        }

        // ============================ HELPERS ============================

        private static string ResolveCollName(IOrganizationService svc, Entity product, Dictionary<Guid, string> cache)
        {
            if (!(product.Contains("new_collection") && product["new_collection"] is EntityReference r)) return "";
            if (cache.TryGetValue(r.Id, out string n)) return n;
            try { var c = svc.Retrieve(r.LogicalName, r.Id, new ColumnSet("new_name")); n = GetString(c, "new_name"); }
            catch { n = r.Name ?? ""; }
            cache[r.Id] = n; return n;
        }

        private static List<Src> ReadCsv(string path)
        {
            var list = new List<Src>();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] f = ParseCsvLine(lines[i]);
                if (f.Length < 4) continue;
                list.Add(new Src { Name = f[0].Trim(), Collection = f[1].Trim(), External = f[2].Trim(), Internal = f[3].Trim() });
            }
            return list;
        }

        private static string[] ParseCsvLine(string line)
        {
            var res = new List<string>(); var cur = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(c); }
                else { if (c == '"') q = true; else if (c == ',') { res.Add(cur.ToString()); cur.Clear(); } else cur.Append(c); }
            }
            res.Add(cur.ToString());
            return res.ToArray();
        }

        private static string Norm(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        private static List<Entity> RetrieveAll(IOrganizationService service, QueryExpression q)
        {
            var all = new List<Entity>();
            q.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, PagingCookie = null };
            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(q);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                q.PageInfo.PageNumber++; q.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        private static Guid GetLookupId(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? r.Id : Guid.Empty;
        private static string GetString(Entity e, string a) => e.Contains(a) && e[a] != null ? e[a].ToString() : "";
        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
        private static void Err(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  ERROR " + m); Console.ResetColor(); }
        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }

        private class Src { public string Name, Collection, External, Internal; }
        private class InvPlan { public Entity Inv; public bool SetExternal, SetInternal; }
        private class Plan
        {
            public Entity Product; public Src Src;
            public bool SetProdExternal, SetProdInternal;
            public List<InvPlan> InvUpdates = new List<InvPlan>();
        }
    }
}
