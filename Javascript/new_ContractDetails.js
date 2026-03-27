function js_Details(executionContext)
{

var formContext = executionContext.getFormContext();	
var bha = formContext.getAttribute("new_breasthealthawareness");
var fa = formContext.getAttribute("new_fanappreciation");
var fd = formContext.getAttribute("new_fathersday");
var gg = formContext.getAttribute("new_gogreen");
var hft = formContext.getAttribute("new_hoopsfortroops");
var nan = formContext.getAttribute("new_nativeamericannight");
var on = formContext.getAttribute("new_openingnight");
var p = formContext.getAttribute("new_pride");
var pa = formContext.getAttribute("new_proam");
var iw = formContext.getAttribute("new_inspiringwomen");
var yfn = formContext.getAttribute("new_youthfitnessnight");
var na1 = formContext.getAttribute("new_nonea");

var array1 = (+bha+fa+fd+gg+hft+nan+on+p+pa+iw+yfn+na1);

var as = formContext.getAttribute("new_arenasignage");
var de = formContext.getAttribute("new_digitalemail");
var ds = formContext.getAttribute("new_digitalsocial");
var dw = formContext.getAttribute("new_digitalweb");
var fe = formContext.getAttribute("new_fanexperience");
var igp = formContext.getAttribute("new_ingamepromo");
var pr = formContext.getAttribute("new_print");
var th = formContext.getAttribute("new_ticketshospitality");
var tv = formContext.getAttribute("new_tv");
var tvs = formContext.getAttribute("new_tvsignage");
var na2 = formContext.getAttribute("new_noneb");

var array2 = (+as+de+ds+dw+fe+igp+pr+th+tv+tvs+na2);

var bcc = formContext.getAttribute("new_basketballcampclinic");
var cfk = formContext.getAttribute("new_courtsforkids");
var tfk = formContext.getAttribute("new_ticketsforkids");
var sf = formContext.getAttribute("new_stormfoundation");
var na3 = formContext.getAttribute("new_nonec");

var array3 = (+bcc+cfk+tfk+sf+na3);

var changeFields = ["bha","fa","fd","gg","hft","nan","on","p","pa","iw","yfn","na1","as","de","ds","dw","fe","igp","pr","th","tv","tvs","na2","bcc","cfk","tfk","sf","na3","new_salesstage"];	
	
	if (formContext.getAttribute("new_salesstage").getValue() == 100000003)
		{
	try
	{
			if (array1 == 0)
			{
               bha.setRequiredLevel("required");
			   fa.setRequiredLevel("required");
			   fd.setRequiredLevel("required");
			   gg.setRequiredLevel("required");
			   hft.setRequiredLevel("required");
			   nan.setRequiredLevel("required");
			   on.setRequiredLevel("required");
			   p.setRequiredLevel("required");
			   pa.setRequiredLevel("required");
			   iw.setRequiredLevel("required");
			   yfn.setRequiredLevel("required");
			   na1.setRequiredLevel("required");
			}
			else
			{
			   bha.setRequiredLevel("none");
			   fa.setRequiredLevel("none");
			   fd.setRequiredLevel("none");
			   gg.setRequiredLevel("none");
			   hft.setRequiredLevel("none");
			   nan.setRequiredLevel("none");
			   on.setRequiredLevel("none");
			   p.setRequiredLevel("none");
			   pa.setRequiredLevel("none");
			   iw.setRequiredLevel("none");
			   yfn.setRequiredLevel("none");
			   na1.setRequiredLevel("none");
            }
			if (array2 == 0)
			{
			   as.setRequiredLevel("required");
			   de.setRequiredLevel("required");
			   ds.setRequiredLevel("required");
			   dw.setRequiredLevel("required");
			   fe.setRequiredLevel("required");
			   igp.setRequiredLevel("required");
			   pr.setRequiredLevel("required");
			   th.setRequiredLevel("required");
			   tv.setRequiredLevel("required");
			   tvs.setRequiredLevel("required");
			   na2.setRequiredLevel("required");
			}
			else
			{
			   as.setRequiredLevel("none");
			   de.setRequiredLevel("none");
			   ds.setRequiredLevel("none");
			   dw.setRequiredLevel("none");
			   fe.setRequiredLevel("none");
			   igp.setRequiredLevel("none");
			   pr.setRequiredLevel("none");
			   th.setRequiredLevel("none");
			   tv.setRequiredLevel("none");
			   tvs.setRequiredLevel("none");
			   na2.setRequiredLevel("none");			
			}
			if (array3 == 0)
			{
			   bcc.setRequiredLevel("required");
			   cfk.setRequiredLevel("required");
			   tfk.setRequiredLevel("required");
			   sf.setRequiredLevel("required");
			   na3.setRequiredLevel("required");
			}
			else
		    {
			   bcc.setRequiredLevel("none");
			   cfk.setRequiredLevel("none");
			   tfk.setRequiredLevel("none");
			   sf.setRequiredLevel("none");
			   na3.setRequiredLevel("none");
			}

	}
	
	catch (e)
	{
		alert(e.message);
	}

		}
}
for (index = 0; index < changeFields.length; index++) {
	}


function js_cTotal(executionContext)
{

var formContext = executionContext.getFormContext();
var yr1 = formContext.getAttribute("new_year1").getValue();
var yr2 = formContext.getAttribute("new_year2").getValue();
var yr3 = formContext.getAttribute("new_year3").getValue();
var yr4 = formContext.getAttribute("new_year4").getValue();
var yr5 = formContext.getAttribute("new_year5").getValue();
var es2 = formContext.getAttribute("new_esc2").getValue();
var es3 = formContext.getAttribute("new_esc3").getValue();
var es4 = formContext.getAttribute("new_esc4").getValue();
var es5 = formContext.getAttribute("new_esc5").getValue();

var ctTotal = (yr1+(yr2+(yr2*(es2/100)))+(yr3+(yr3*(es3/100)))+(yr4+(yr4*(es4/100)))+(yr5+(yr5*(es5/100))));

var amtChange = ["new_year1","new_year2","new_year3","new_year4","new_year5","new_esc2","new_esc3","new_esc4","new_esc5"];

	if (formContext.getAttribute("new_salesstage").getValue() == 100000003)
		{
			formContext.getAttribute("new_totalrevenue").setValue(ctTotal);
		}
for (index = 0; index < amtChange.length; index++) {
	}
}