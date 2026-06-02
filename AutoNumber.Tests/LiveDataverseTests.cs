/*
 * Live integration tests against a real Dataverse environment.
 *
 * These tests are gated behind the [Category("Live")] filter and are skipped
 * unless the DATAVERSE_URL and DATAVERSE_TOKEN environment variables are set.
 *
 * The token is acquired by the workflow (.github/workflows/live-tests.yml) via
 * passwordless OIDC + a Federated Credential on the Azure AD app registration:
 *
 *   azure/login@v2 (federated)  →  az account get-access-token  →  $env:DATAVERSE_TOKEN
 *
 * For local runs:
 *   az login
 *   $env:DATAVERSE_URL    = "https://orgxxxxxxxx.crm4.dynamics.com"
 *   $env:DATAVERSE_TOKEN  = (az account get-access-token --resource $env:DATAVERSE_URL --query accessToken -o tsv)
 *   nunit3-console AutoNumber.Tests.dll --where "cat == Live"
 *
 * The AutoNumber managed solution must already be deployed to the target org;
 * these tests exercise the deployed plugins, they don't deploy them.
 *
 * Setup steps (Azure AD app, Federated Credential, Dataverse application user):
 * see LiveTests.md.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.WebServiceClient;

using NUnit.Framework;

namespace Celedon
{
	[TestFixture]
	[Category("Live")]
	[NonParallelizable]  // The plugin step is shared per (entity, event); tests must run serially.
	public class LiveDataverseTests
	{
		private const string TargetEntity = "account";
		private const string TargetAttr   = "accountnumber";
		private const int    PluginAsyncTimeoutSeconds = 90;

		private OrganizationWebProxyClient _client;
		private readonly List<EntityReference> _toCleanup = new List<EntityReference>();

		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
			var url   = Environment.GetEnvironmentVariable("DATAVERSE_URL");
			var token = Environment.GetEnvironmentVariable("DATAVERSE_TOKEN");

			if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
			{
				Assert.Ignore(
					"Live tests skipped: set DATAVERSE_URL and DATAVERSE_TOKEN to run against a real org. " +
					"See LiveTests.md for setup.");
				return;
			}

			var serviceUri = new Uri(
				new Uri(url.TrimEnd('/') + "/"),
				"XRMServices/2011/Organization.svc/web?SdkClientVersion=9.0");

			_client = new OrganizationWebProxyClient(serviceUri, false)
			{
				HeaderToken = token,
			};

			// Sanity ping — fail fast with a clear message if creds/perm are wrong.
			try
			{
				_client.Execute(new Microsoft.Crm.Sdk.Messages.WhoAmIRequest());
			}
			catch (Exception ex)
			{
				Assert.Fail(
					$"Could not connect to Dataverse at {url}. " +
					"Verify DATAVERSE_URL, the bearer token, and that the Application User has been created. " +
					$"Underlying error: {ex.Message}");
			}
		}

		[OneTimeTearDown]
		public void OneTimeTearDown()
		{
			try { _client?.Dispose(); } catch { /* swallow */ }
		}

		[TearDown]
		public void TearDown()
		{
			// Best-effort cleanup in reverse creation order.  Deleting cel_autonumber
			// records triggers DeleteAutoNumber, which removes the plugin step when
			// no records remain.
			foreach (var er in ((IEnumerable<EntityReference>)_toCleanup).Reverse())
			{
				try { _client.Delete(er.LogicalName, er.Id); } catch { /* swallow */ }
			}
			_toCleanup.Clear();
		}

		#region Tests

		[Test]
		public void Update_event_step_gets_filteringattributes_set()
		{
			const string triggerAttr = "name";

			CreateAutoNumber(eventCode: 1, triggerAttribute: triggerAttr,
				targetAttribute: TargetAttr, prefix: "CI-U-", digits: 4, nextNumber: 1);

			var step = WaitForStepWithFilter(StepName(updateEvent: true), triggerAttr, TargetAttr);
			var attrs = ParseFilter(step);

			Assert.That(attrs, Is.SupersetOf(new[] { triggerAttr, TargetAttr }),
				"Update step must filter on trigger attribute and target attribute.");
		}

		[Test]
		public void Create_event_step_has_no_filteringattributes()
		{
			CreateAutoNumber(eventCode: 0, triggerAttribute: null,
				targetAttribute: TargetAttr, prefix: "CI-C-", digits: 4, nextNumber: 1);

			var step = WaitForStep(StepName(updateEvent: false));
			var filter = step.GetAttributeValue<string>("filteringattributes");

			Assert.That(string.IsNullOrEmpty(filter), Is.True,
				"Create-event step must not declare filteringattributes (saw: '{0}').", filter ?? "");
		}

		[Test]
		public void Adding_a_second_update_record_merges_filteringattributes_into_shared_step()
		{
			CreateAutoNumber(eventCode: 1, triggerAttribute: "name",
				targetAttribute: TargetAttr, prefix: "CI-M1-", digits: 4, nextNumber: 1);
			WaitForStepWithFilter(StepName(updateEvent: true), "name", TargetAttr);

			// Second record uses a distinct text target ("fax") and trigger ("industrycode").
			// The target must be a String/Memo field — ValidateAutoNumber rejects non-text targets.
			CreateAutoNumber(eventCode: 1, triggerAttribute: "industrycode",
				targetAttribute: "fax", prefix: "CI-M2-", digits: 4, nextNumber: 1);

			var step = WaitForStepWithFilter(StepName(updateEvent: true),
				"name", TargetAttr, "industrycode", "fax");
			var attrs = ParseFilter(step);

			Assert.That(attrs, Is.SupersetOf(new[] { "name", TargetAttr, "industrycode", "fax" }),
				"Step must merge filtering attributes from both records.");
		}

		[Test]
		public void Deleting_last_autonumber_record_removes_the_step()
		{
			var id = CreateAutoNumber(eventCode: 0, triggerAttribute: null,
				targetAttribute: TargetAttr, prefix: "CI-D-", digits: 4, nextNumber: 1);
			WaitForStep(StepName(updateEvent: false));

			// Explicit delete here (not via TearDown) so we can verify the step is gone.
			_toCleanup.RemoveAll(er => er.Id == id);
			_client.Delete("cel_autonumber", id);

			var stepName = StepName(updateEvent: false);
			Assert.That(WaitForStepRemoval(stepName), Is.True,
				$"Step '{stepName}' should be removed after the last autonumber record is deleted.");
		}

		[Test]
		public void Newly_created_account_gets_autonumber_assigned_end_to_end()
		{
			CreateAutoNumber(eventCode: 0, triggerAttribute: null,
				targetAttribute: TargetAttr, prefix: "CI-E2E-", digits: 4, nextNumber: 1);
			WaitForStep(StepName(updateEvent: false));

			var account = new Entity("account") { ["name"] = "ci-test-" + Guid.NewGuid() };
			var accountId = _client.Create(account);
			_toCleanup.Add(new EntityReference("account", accountId));

			var stored = _client.Retrieve("account", accountId, new ColumnSet(TargetAttr));
			Assert.That(stored.GetAttributeValue<string>(TargetAttr),
				Does.StartWith("CI-E2E-").And.Match(@"^CI-E2E-\d{4}$"),
				"Plugin should have populated accountnumber on Create.");
		}

		[Test]
		public void OnDemand_action_assigns_number_to_existing_record()
		{
			const string attr = "fax";

			// Update-event config so the Create pipeline does NOT auto-populate the field —
			// this isolates the on-demand path. The field stays empty on Create.
			var configId = CreateAutoNumber(eventCode: 1, triggerAttribute: "telephone1",
				targetAttribute: attr, prefix: "CI-OD-", digits: 4, nextNumber: 1);

			var account = new Entity(TargetEntity) { ["name"] = "ci-od-" + Guid.NewGuid() };
			var accountId = _client.Create(account);
			_toCleanup.Add(new EntityReference(TargetEntity, accountId));

			Assert.That(_client.Retrieve(TargetEntity, accountId, new ColumnSet(attr)).GetAttributeValue<string>(attr),
				Is.Null.Or.Empty, "Precondition: the field must be empty before the on-demand call.");

			var req = new OrganizationRequest("cel_GenerateAutoNumber");
			req["TargetEntity"] = TargetEntity;
			req["TargetId"] = accountId.ToString();
			req["AutoNumberConfigId"] = configId.ToString();
			var number = (string)_client.Execute(req).Results["Number"];

			Assert.That(number, Does.Match(@"^CI-OD-\d{4}$"), "Action must return the generated number.");

			var stored = _client.Retrieve(TargetEntity, accountId, new ColumnSet(attr));
			Assert.That(stored.GetAttributeValue<string>(attr), Is.EqualTo(number),
				"The generated number must be written onto the target field.");
		}

		[Test]
		public void CreateMultiple_assigns_unique_sequential_numbers_to_the_whole_batch()
		{
			const int n = 25;
			var configId = CreateAutoNumber(eventCode: 0, triggerAttribute: null,
				targetAttribute: TargetAttr, prefix: "CI-BULK-", digits: 5, nextNumber: 1);

			// The bulk step is registered alongside the single step by CreateAutoNumber.
			WaitForStep(StepName(updateEvent: false) + " (CreateMultiple)");

			var targets = new EntityCollection { EntityName = TargetEntity };
			for (var i = 0; i < n; i++)
			{
				targets.Entities.Add(new Entity(TargetEntity) { ["name"] = "ci-bulk-" + Guid.NewGuid() });
			}

			var req = new OrganizationRequest("CreateMultiple") { ["Targets"] = targets };
			var ids = (Guid[])_client.Execute(req).Results["Ids"];
			foreach (var id in ids)
			{
				_toCleanup.Add(new EntityReference(TargetEntity, id));
			}

			Assert.That(ids.Length, Is.EqualTo(n));

			var numbers = ids
				.Select(id => _client.Retrieve(TargetEntity, id, new ColumnSet(TargetAttr)).GetAttributeValue<string>(TargetAttr))
				.ToList();

			Assert.That(numbers.All(x => !string.IsNullOrEmpty(x) && x.StartsWith("CI-BULK-") && x.Length == "CI-BULK-".Length + 5),
				Is.True, "Every record in the batch must receive a formatted number.");
			Assert.That(numbers.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(n),
				"All numbers in the batch must be unique (no duplicates, no fan-out double-assignment).");

			// The counter must advance by exactly the batch size — proving the single-record step did
			// NOT also fan out for this CreateMultiple (which would advance it by 2N).
			var nextNumber = _client.Retrieve("cel_autonumber", configId, new ColumnSet("cel_nextnumber"))
				.GetAttributeValue<int>("cel_nextnumber");
			Assert.That(nextNumber, Is.EqualTo(1 + n),
				"cel_nextnumber must advance by exactly the batch size (single increment, no fan-out duplication).");
		}

		#endregion

		#region Helpers

		private static string StepName(bool updateEvent)
		{
			var name = $"CeledonPartners.AutoNumber.{TargetEntity}";
			return updateEvent ? name + " Update" : name;
		}

		private Guid CreateAutoNumber(int eventCode, string triggerAttribute,
			string targetAttribute, string prefix, int digits, int nextNumber)
		{
			var record = new Entity("cel_autonumber");
			record["cel_entityname"]    = TargetEntity;
			record["cel_attributename"] = targetAttribute;
			record["cel_triggerevent"]  = new OptionSetValue(eventCode);
			record["cel_prefix"]        = prefix;
			record["cel_digits"]        = digits;
			record["cel_nextnumber"]    = nextNumber;
			if (!string.IsNullOrEmpty(triggerAttribute))
			{
				record["cel_triggerattribute"] = triggerAttribute;
			}

			var id = _client.Create(record);
			_toCleanup.Add(new EntityReference("cel_autonumber", id));
			return id;
		}

		private static string[] ParseFilter(Entity step)
		{
			var raw = step.GetAttributeValue<string>("filteringattributes") ?? string.Empty;
			return raw.Split(',').Select(a => a.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
		}

		private Entity QueryStep(string stepName)
		{
			var result = _client.RetrieveMultiple(new QueryExpression("sdkmessageprocessingstep")
			{
				ColumnSet = new ColumnSet("name", "filteringattributes"),
				Criteria = new FilterExpression
				{
					Conditions = { new ConditionExpression("name", ConditionOperator.Equal, stepName) }
				}
			});
			return result.Entities.FirstOrDefault();
		}

		private Entity WaitForStep(string stepName)
		{
			var deadline = DateTime.UtcNow.AddSeconds(PluginAsyncTimeoutSeconds);
			while (DateTime.UtcNow < deadline)
			{
				var step = QueryStep(stepName);
				if (step != null) return step;
				Thread.Sleep(2000);
			}
			throw new TimeoutException(
				$"Plugin step '{stepName}' did not appear within {PluginAsyncTimeoutSeconds}s. " +
				"Is the AutoNumber solution deployed and CreateAutoNumber registered as Async?");
		}

		private Entity WaitForStepWithFilter(string stepName, params string[] expected)
		{
			var want = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
			var deadline = DateTime.UtcNow.AddSeconds(PluginAsyncTimeoutSeconds);
			while (DateTime.UtcNow < deadline)
			{
				var step = QueryStep(stepName);
				if (step != null)
				{
					var have = new HashSet<string>(ParseFilter(step), StringComparer.OrdinalIgnoreCase);
					if (want.IsSubsetOf(have)) return step;
				}
				Thread.Sleep(2000);
			}
			throw new TimeoutException(
				$"Step '{stepName}' did not gain expected filtering attributes [{string.Join(", ", expected)}] " +
				$"within {PluginAsyncTimeoutSeconds}s.");
		}

		private bool WaitForStepRemoval(string stepName)
		{
			var deadline = DateTime.UtcNow.AddSeconds(PluginAsyncTimeoutSeconds);
			while (DateTime.UtcNow < deadline)
			{
				if (QueryStep(stepName) == null) return true;
				Thread.Sleep(2000);
			}
			return false;
		}

		#endregion
	}
}
