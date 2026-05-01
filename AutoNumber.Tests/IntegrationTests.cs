/*
 * Integration tests for the AutoNumber plugins.
 *
 * These tests exercise CreateAutoNumber / GetNextAutoNumber / DeleteAutoNumber /
 * ValidateAutoNumber end-to-end through their public Execute(IServiceProvider)
 * surface. The Dataverse runtime is replaced by an in-memory fake
 * IOrganizationService that can answer the QueryExpression-shaped queries the
 * plugins emit through OrganizationServiceContext.CreateQuery(...).
 */

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

using Moq;
using NUnit.Framework;

namespace Celedon
{
	#region In-memory IOrganizationService fake

	/// <summary>
	/// Minimal in-memory IOrganizationService that supports the QueryExpression
	/// shapes produced by the OrganizationServiceContext LINQ provider for the
	/// AutoNumber plugins (Equal / AND / OR conditions, single Order).
	/// </summary>
	internal sealed class FakeOrganizationService : IOrganizationService
	{
		private readonly Dictionary<string, Dictionary<Guid, Entity>> _store =
			new Dictionary<string, Dictionary<Guid, Entity>>(StringComparer.OrdinalIgnoreCase);

		public List<OrganizationRequest> ExecuteCalls { get; } = new List<OrganizationRequest>();
		public List<Entity> CreateCalls { get; } = new List<Entity>();
		public List<Entity> UpdateCalls { get; } = new List<Entity>();
		public List<Tuple<string, Guid>> DeleteCalls { get; } = new List<Tuple<string, Guid>>();

		public Func<OrganizationRequest, OrganizationResponse> ExecuteHandler { get; set; }

		public Dictionary<Guid, Entity> Bucket(string entityName)
		{
			if (!_store.TryGetValue(entityName, out var bucket))
			{
				bucket = new Dictionary<Guid, Entity>();
				_store[entityName] = bucket;
			}
			return bucket;
		}

		public Entity Seed(string logicalName, Guid id, params object[] keyValuePairs)
		{
			var entity = new Entity(logicalName) { Id = id };
			for (var i = 0; i + 1 < keyValuePairs.Length; i += 2)
			{
				entity[(string)keyValuePairs[i]] = keyValuePairs[i + 1];
			}
			Bucket(logicalName)[id] = entity;
			return entity;
		}

		public Entity GetStored(string entityName, Guid id)
		{
			return Bucket(entityName).TryGetValue(id, out var e) ? e : null;
		}

		public IEnumerable<Entity> AllOf(string entityName)
		{
			return _store.TryGetValue(entityName, out var b) ? b.Values : Enumerable.Empty<Entity>();
		}

		public Guid Create(Entity entity)
		{
			var id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id;
			entity.Id = id;
			CreateCalls.Add(Clone(entity));
			Bucket(entity.LogicalName)[id] = Clone(entity);
			return id;
		}

		public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
		{
			if (!Bucket(entityName).TryGetValue(id, out var stored))
			{
				throw new InvalidOperationException($"FakeOrganizationService: {entityName} {id} not found.");
			}

			if (columnSet == null || columnSet.AllColumns)
			{
				return Clone(stored);
			}

			var copy = new Entity(stored.LogicalName) { Id = stored.Id };
			foreach (var col in columnSet.Columns)
			{
				if (stored.Contains(col)) copy[col] = stored[col];
			}
			return copy;
		}

		public void Update(Entity entity)
		{
			UpdateCalls.Add(Clone(entity));
			var bucket = Bucket(entity.LogicalName);
			if (!bucket.TryGetValue(entity.Id, out var stored))
			{
				throw new InvalidOperationException($"FakeOrganizationService: {entity.LogicalName} {entity.Id} not found.");
			}
			foreach (var attr in entity.Attributes)
			{
				stored[attr.Key] = attr.Value;
			}
		}

		public void Delete(string entityName, Guid id)
		{
			DeleteCalls.Add(Tuple.Create(entityName, id));
			Bucket(entityName).Remove(id);
		}

		public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
		{
			throw new NotSupportedException();
		}

		public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
		{
			throw new NotSupportedException();
		}

		public EntityCollection RetrieveMultiple(QueryBase query)
		{
			if (!(query is QueryExpression qe))
			{
				throw new NotSupportedException("FakeOrganizationService only supports QueryExpression.");
			}

			var matches = AllOf(qe.EntityName).Where(e => Matches(e, qe.Criteria)).ToList();

			if (qe.Orders != null && qe.Orders.Count > 0)
			{
				var order = qe.Orders[0];
				IComparer<object> comparer = ObjectComparer.Instance;
				matches = order.OrderType == OrderType.Ascending
					? matches.OrderBy(e => Unwrap(e.Contains(order.AttributeName) ? e[order.AttributeName] : null), comparer).ToList()
					: matches.OrderByDescending(e => Unwrap(e.Contains(order.AttributeName) ? e[order.AttributeName] : null), comparer).ToList();
			}

			var result = new EntityCollection();
			foreach (var e in matches) result.Entities.Add(Clone(e));
			return result;
		}

		public OrganizationResponse Execute(OrganizationRequest request)
		{
			ExecuteCalls.Add(request);

			// The OrganizationServiceContext LINQ provider routes RetrieveMultiple (and a few
			// others) through Execute, not through the typed IOrganizationService methods.
			// Handle the common ones internally so ExecuteHandler only sees genuinely-unhandled
			// requests (typically metadata calls in tests that need to stub them).
			switch (request)
			{
				case RetrieveMultipleRequest rm:
					return new RetrieveMultipleResponse
					{
						Results = { ["EntityCollection"] = RetrieveMultiple((QueryBase)rm["Query"]) }
					};
				case RetrieveRequest rr:
					var rTarget = (EntityReference)rr["Target"];
					return new RetrieveResponse
					{
						Results = { ["Entity"] = Retrieve(rTarget.LogicalName, rTarget.Id, (ColumnSet)rr["ColumnSet"]) }
					};
				case CreateRequest cr:
					return new CreateResponse
					{
						Results = { ["id"] = Create((Entity)cr["Target"]) }
					};
				case UpdateRequest ur:
					Update((Entity)ur["Target"]);
					return new UpdateResponse();
				case DeleteRequest dr:
					var dTarget = (EntityReference)dr["Target"];
					Delete(dTarget.LogicalName, dTarget.Id);
					return new DeleteResponse();
			}

			if (ExecuteHandler != null) return ExecuteHandler(request);
			throw new NotSupportedException($"FakeOrganizationService: Execute({request.RequestName}) was not configured.");
		}

		private static bool Matches(Entity entity, FilterExpression filter)
		{
			if (filter == null) return true;

			var results = new List<bool>();
			foreach (var c in filter.Conditions) results.Add(MatchCondition(entity, c));
			foreach (var f in filter.Filters) results.Add(Matches(entity, f));

			if (results.Count == 0) return true;
			return filter.FilterOperator == LogicalOperator.Or
				? results.Any(b => b)
				: results.All(b => b);
		}

		private static bool MatchCondition(Entity entity, ConditionExpression c)
		{
			var actual = Unwrap(entity.Contains(c.AttributeName) ? entity[c.AttributeName] : null);
			switch (c.Operator)
			{
				case ConditionOperator.Equal:
					return c.Values.Any(v => ValueEquals(Unwrap(v), actual));
				case ConditionOperator.NotEqual:
					return !c.Values.Any(v => ValueEquals(Unwrap(v), actual));
				case ConditionOperator.Null:
					return actual == null;
				case ConditionOperator.NotNull:
					return actual != null;
				default:
					throw new NotSupportedException($"FakeOrganizationService: ConditionOperator.{c.Operator} not supported.");
			}
		}

		private static object Unwrap(object value)
		{
			switch (value)
			{
				case OptionSetValue osv: return osv.Value;
				case EntityReference er: return er.Id;
				case Money m: return m.Value;
				default: return value;
			}
		}

		private static bool ValueEquals(object a, object b)
		{
			if (a == null && b == null) return true;
			if (a == null || b == null) return false;
			if (a.GetType() == b.GetType()) return a.Equals(b);
			// Tolerate int/long/short via conversion
			try { return Convert.ToString(a) == Convert.ToString(b); }
			catch { return a.Equals(b); }
		}

		private static Entity Clone(Entity e)
		{
			var copy = new Entity(e.LogicalName) { Id = e.Id };
			foreach (var attr in e.Attributes) copy[attr.Key] = attr.Value;
			return copy;
		}

		private sealed class ObjectComparer : IComparer<object>
		{
			public static readonly ObjectComparer Instance = new ObjectComparer();

			public int Compare(object x, object y)
			{
				if (ReferenceEquals(x, y)) return 0;
				if (x == null) return -1;
				if (y == null) return 1;
				if (x is IComparable c) return c.CompareTo(y);
				return string.Compare(x.ToString(), y.ToString(), StringComparison.Ordinal);
			}
		}
	}

	#endregion

	#region Plugin test harness

	/// <summary>
	/// Builds the IServiceProvider/IPluginExecutionContext that
	/// CeledonPlugin.Execute expects, wired to a FakeOrganizationService.
	/// </summary>
	internal sealed class PluginHarness
	{
		public FakeOrganizationService Service { get; } = new FakeOrganizationService();
		public ParameterCollection InputParameters { get; } = new ParameterCollection();
		public ParameterCollection OutputParameters { get; } = new ParameterCollection();
		public EntityImageCollection PreEntityImages { get; } = new EntityImageCollection();
		public EntityImageCollection PostEntityImages { get; } = new EntityImageCollection();

		public string MessageName { get; set; }
		public int Stage { get; set; }
		public string PrimaryEntityName { get; set; }
		public Guid UserId { get; set; } = Guid.NewGuid();

		public IServiceProvider Build()
		{
			var pluginContext = new Mock<IPluginExecutionContext>();
			pluginContext.SetupGet(c => c.MessageName).Returns(() => MessageName);
			pluginContext.SetupGet(c => c.Stage).Returns(() => Stage);
			pluginContext.SetupGet(c => c.PrimaryEntityName).Returns(() => PrimaryEntityName);
			pluginContext.SetupGet(c => c.InputParameters).Returns(InputParameters);
			pluginContext.SetupGet(c => c.OutputParameters).Returns(OutputParameters);
			pluginContext.SetupGet(c => c.PreEntityImages).Returns(PreEntityImages);
			pluginContext.SetupGet(c => c.PostEntityImages).Returns(PostEntityImages);
			pluginContext.SetupGet(c => c.UserId).Returns(() => UserId);
			pluginContext.SetupGet(c => c.InitiatingUserId).Returns(() => UserId);
			pluginContext.SetupGet(c => c.CorrelationId).Returns(Guid.NewGuid());

			var factory = new Mock<IOrganizationServiceFactory>();
			factory.Setup(f => f.CreateOrganizationService(It.IsAny<Guid?>())).Returns(Service);

			var tracing = new Mock<ITracingService>();

			var provider = new Mock<IServiceProvider>();
			provider.Setup(p => p.GetService(typeof(IPluginExecutionContext))).Returns(pluginContext.Object);
			provider.Setup(p => p.GetService(typeof(IOrganizationServiceFactory))).Returns(factory.Object);
			provider.Setup(p => p.GetService(typeof(ITracingService))).Returns(tracing.Object);

			return provider.Object;
		}
	}

	#endregion

	#region CreateAutoNumber

	[TestFixture]
	public class CreateAutoNumberTests
	{
		private const string EntityName = "account";
		private const string TargetAttr = "accountnumber";
		private const string TriggerAttr = "name";
		private const string ConditionAttr = "statuscode";

		private PluginHarness _harness;
		private Guid _pluginTypeId;
		private Guid _createMessageId;
		private Guid _updateMessageId;
		private Guid _createFilterId;
		private Guid _updateFilterId;

		[SetUp]
		public void SetUp()
		{
			_harness = new PluginHarness
			{
				MessageName = Constants.PipelineMessage.Create,
				Stage = Constants.PipelineStage.PostOperation,
				PrimaryEntityName = "cel_autonumber",
			};

			_pluginTypeId = Guid.NewGuid();
			_createMessageId = Guid.NewGuid();
			_updateMessageId = Guid.NewGuid();
			_createFilterId = Guid.NewGuid();
			_updateFilterId = Guid.NewGuid();

			_harness.Service.Seed("plugintype", _pluginTypeId,
				"name", typeof(GetNextAutoNumber).FullName,
				"plugintypeid", _pluginTypeId);

			_harness.Service.Seed("sdkmessage", _createMessageId,
				"name", "Create",
				"sdkmessageid", _createMessageId);

			_harness.Service.Seed("sdkmessage", _updateMessageId,
				"name", "Update",
				"sdkmessageid", _updateMessageId);

			_harness.Service.Seed("sdkmessagefilter", _createFilterId,
				"primaryobjecttypecode", EntityName,
				"sdkmessageid", new EntityReference("sdkmessage", _createMessageId),
				"sdkmessagefilterid", _createFilterId);

			_harness.Service.Seed("sdkmessagefilter", _updateFilterId,
				"primaryobjecttypecode", EntityName,
				"sdkmessageid", new EntityReference("sdkmessage", _updateMessageId),
				"sdkmessagefilterid", _updateFilterId);
		}

		private Entity NewAutoNumberRecord(int triggerEvent, string targetAttr = TargetAttr,
			string triggerAttr = null, string conditionAttr = null)
		{
			var target = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			target["cel_entityname"] = EntityName;
			target["cel_attributename"] = targetAttr;
			target["cel_triggerevent"] = new OptionSetValue(triggerEvent);
			if (triggerAttr != null) target["cel_triggerattribute"] = triggerAttr;
			if (conditionAttr != null) target["cel_conditionaloptionset"] = conditionAttr;
			return target;
		}

		private void RunCreate(Entity target)
		{
			_harness.InputParameters["Target"] = target;
			new CreateAutoNumber().Execute(_harness.Build());
		}

		[Test]
		public void Create_event_step_is_registered_without_filtering_attributes()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 0));

			var step = _harness.Service.AllOf("sdkmessageprocessingstep").Single();
			Assert.That(step.GetAttributeValue<string>("name"), Is.EqualTo("CeledonPartners.AutoNumber.account"));
			Assert.That(step.Contains("filteringattributes"), Is.False,
				"Create-event steps should not declare filtering attributes.");
			Assert.That(step.GetAttributeValue<EntityReference>("sdkmessageid").Id, Is.EqualTo(_createMessageId));

			Assert.That(_harness.Service.AllOf("sdkmessageprocessingstepimage"), Is.Empty,
				"Create steps don't need a PreImage.");
		}

		[Test]
		public void Update_event_step_filters_on_trigger_target_and_conditional_attributes()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 1,
				triggerAttr: TriggerAttr, conditionAttr: ConditionAttr));

			var step = _harness.Service.AllOf("sdkmessageprocessingstep").Single();
			Assert.That(step.GetAttributeValue<string>("name"),
				Is.EqualTo("CeledonPartners.AutoNumber.account Update"));

			var filter = step.GetAttributeValue<string>("filteringattributes");
			var attrs = filter.Split(',').Select(a => a.Trim()).ToList();
			Assert.That(attrs, Is.EquivalentTo(new[] { TriggerAttr, TargetAttr, ConditionAttr }));
		}

		[Test]
		public void Update_event_without_trigger_attribute_still_filters_on_target_attribute()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 1, triggerAttr: null, conditionAttr: null));

			var step = _harness.Service.AllOf("sdkmessageprocessingstep").Single();
			var filter = step.GetAttributeValue<string>("filteringattributes");
			Assert.That(filter.Split(',').Select(a => a.Trim()), Is.EquivalentTo(new[] { TargetAttr }));
		}

		[Test]
		public void Update_event_creates_PreImage_with_target_attribute()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 1, triggerAttr: TriggerAttr));

			var image = _harness.Service.AllOf("sdkmessageprocessingstepimage").Single();
			Assert.That(image.GetAttributeValue<OptionSetValue>("imagetype").Value, Is.EqualTo(0));
			Assert.That(image.GetAttributeValue<string>("attributes"), Is.EqualTo(TargetAttr));
			Assert.That(image.GetAttributeValue<string>("messagepropertyname"), Is.EqualTo("Target"));
			Assert.That(image.GetAttributeValue<string>("entityalias"), Is.EqualTo("Image"));
		}

		[Test]
		public void Adding_a_second_update_record_merges_filtering_attributes_into_existing_step()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 1, triggerAttr: TriggerAttr));

			// Reset call lists, then add a second autonumber record on the same entity with a
			// different trigger attribute.
			_harness.Service.UpdateCalls.Clear();
			_harness.Service.CreateCalls.Clear();

			RunCreate(NewAutoNumberRecord(triggerEvent: 1,
				targetAttr: "accountcategorycode",
				triggerAttr: "industrycode",
				conditionAttr: ConditionAttr));

			Assert.That(_harness.Service.AllOf("sdkmessageprocessingstep").Count(), Is.EqualTo(1),
				"A shared step should still exist (no new step created).");
			Assert.That(_harness.Service.CreateCalls.Any(e => e.LogicalName == "sdkmessageprocessingstep"), Is.False,
				"The second cel_autonumber record must NOT create a duplicate step.");

			var step = _harness.Service.AllOf("sdkmessageprocessingstep").Single();
			var attrs = step.GetAttributeValue<string>("filteringattributes")
				.Split(',').Select(a => a.Trim()).ToList();
			Assert.That(attrs, Is.EquivalentTo(new[]
			{
				TriggerAttr, TargetAttr, ConditionAttr,
				"industrycode", "accountcategorycode"
			}));
		}

		[Test]
		public void Adding_a_second_create_record_keeps_existing_step_unchanged()
		{
			RunCreate(NewAutoNumberRecord(triggerEvent: 0));

			_harness.Service.UpdateCalls.Clear();
			_harness.Service.CreateCalls.Clear();

			RunCreate(NewAutoNumberRecord(triggerEvent: 0, targetAttr: "accountcategorycode"));

			Assert.That(_harness.Service.AllOf("sdkmessageprocessingstep").Count(), Is.EqualTo(1));
			Assert.That(_harness.Service.UpdateCalls, Is.Empty,
				"For Create-event reuse, the existing step is left alone (no merge needed).");
			Assert.That(_harness.Service.CreateCalls, Is.Empty);
		}

		[Test]
		public void Filter_merge_is_idempotent_for_same_record_replayed()
		{
			var record = NewAutoNumberRecord(triggerEvent: 1, triggerAttr: TriggerAttr);
			RunCreate(record);

			// Replay the same record (e.g. after a transient error).  The merge logic should be
			// idempotent: filteringattributes contains exactly the original two values.
			RunCreate(record);

			var step = _harness.Service.AllOf("sdkmessageprocessingstep").Single();
			var attrs = step.GetAttributeValue<string>("filteringattributes")
				.Split(',').Select(a => a.Trim()).ToList();
			Assert.That(attrs, Is.EquivalentTo(new[] { TriggerAttr, TargetAttr }));
		}
	}

	#endregion

	#region GetNextAutoNumber

	[TestFixture]
	public class GetNextAutoNumberTests
	{
		private const string EntityName = "account";
		private const string TargetAttr = "accountnumber";
		private const string TriggerAttr = "name";

		private PluginHarness _harness;

		[SetUp]
		public void SetUp()
		{
			_harness = new PluginHarness
			{
				PrimaryEntityName = EntityName,
				Stage = Constants.PipelineStage.PreOperation,
			};
		}

		private Entity SeedAutoNumber(int triggerEvent, int nextNumber, int digits,
			string prefix, string suffix,
			string triggerAttr = null, string targetAttr = TargetAttr,
			string conditionalAttr = null, int conditionalValue = 0,
			int statecode = 0)
		{
			var id = Guid.NewGuid();
			return _harness.Service.Seed("cel_autonumber", id,
				"cel_autonumberid", id,
				"cel_entityname", EntityName,
				"cel_attributename", targetAttr,
				"cel_triggerevent", new OptionSetValue(triggerEvent),
				"cel_triggerattribute", triggerAttr ?? "",
				"cel_conditionaloptionset", conditionalAttr ?? "",
				"cel_conditionalvalue", conditionalValue,
				"cel_digits", digits,
				"cel_prefix", prefix ?? "",
				"cel_suffix", suffix ?? "",
				"cel_nextnumber", nextNumber,
				"statecode", new OptionSetValue(statecode));
		}

		private Entity RunCreate(Entity target)
		{
			_harness.MessageName = Constants.PipelineMessage.Create;
			_harness.InputParameters["Target"] = target;
			new GetNextAutoNumber("{ \"EntityName\": \"" + EntityName + "\", \"EventName\": \"Create\" }")
				.Execute(_harness.Build());
			return target;
		}

		private Entity RunUpdate(Entity target, Entity preImage)
		{
			_harness.MessageName = Constants.PipelineMessage.Update;
			_harness.InputParameters["Target"] = target;
			_harness.PreEntityImages["Image"] = preImage ?? new Entity(EntityName);
			new GetNextAutoNumber("{ \"EntityName\": \"" + EntityName + "\", \"EventName\": \"Update\" }")
				.Execute(_harness.Build());
			return target;
		}

		[Test]
		public void Create_populates_target_attribute_with_prefix_number_and_increments_counter()
		{
			var autoNumber = SeedAutoNumber(triggerEvent: 0, nextNumber: 42, digits: 4,
				prefix: "ACME-", suffix: "");

			var target = new Entity(EntityName);
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("ACME-0042"));
			var stored = _harness.Service.GetStored("cel_autonumber", autoNumber.Id);
			Assert.That(stored.GetAttributeValue<int>("cel_nextnumber"), Is.EqualTo(43),
				"The counter must increment exactly once per create.");
			Assert.That(stored.GetAttributeValue<string>("cel_preview"), Is.EqualTo("ACME-0042"));
		}

		[Test]
		public void Multiple_autonumber_records_on_same_entity_all_fire()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 1, digits: 3,
				prefix: "A-", suffix: "", targetAttr: "accountnumber");
			SeedAutoNumber(triggerEvent: 0, nextNumber: 7, digits: 2,
				prefix: "B-", suffix: "", targetAttr: "accountcategorycode");

			var target = new Entity(EntityName);
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>("accountnumber"), Is.EqualTo("A-001"));
			Assert.That(target.GetAttributeValue<string>("accountcategorycode"), Is.EqualTo("B-07"));
		}

		[Test]
		public void Manual_value_in_target_is_not_overwritten()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 5, digits: 3, prefix: "X-", suffix: "");

			var target = new Entity(EntityName);
			target[TargetAttr] = "MANUAL";
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("MANUAL"));
		}

		[Test]
		public void Conditional_optionset_match_generates_number()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 1, digits: 2, prefix: "P-", suffix: "",
				conditionalAttr: "statuscode", conditionalValue: 1);

			var target = new Entity(EntityName);
			target["statuscode"] = new OptionSetValue(1);
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("P-01"));
		}

		[Test]
		public void Conditional_optionset_mismatch_skips_number()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 1, digits: 2, prefix: "P-", suffix: "",
				conditionalAttr: "statuscode", conditionalValue: 2);

			var target = new Entity(EntityName);
			target["statuscode"] = new OptionSetValue(1);
			RunCreate(target);

			Assert.That(target.Contains(TargetAttr), Is.False,
				"When the conditional optionset does not match the target value, no number is assigned.");
		}

		[Test]
		public void Update_skips_when_trigger_attribute_is_not_in_target()
		{
			SeedAutoNumber(triggerEvent: 1, nextNumber: 1, digits: 2, prefix: "U-", suffix: "",
				triggerAttr: TriggerAttr);

			var target = new Entity(EntityName);
			// The user changed some unrelated attribute, not the trigger attribute.
			target["description"] = "noise";
			RunUpdate(target, preImage: new Entity(EntityName));

			Assert.That(target.Contains(TargetAttr), Is.False);
		}

		[Test]
		public void Update_skips_when_PreImage_already_has_target_attribute_populated()
		{
			SeedAutoNumber(triggerEvent: 1, nextNumber: 1, digits: 2, prefix: "U-", suffix: "",
				triggerAttr: TriggerAttr);

			var target = new Entity(EntityName);
			target[TriggerAttr] = "Acme";
			var preImage = new Entity(EntityName);
			preImage[TargetAttr] = "ALREADY-SET";

			RunUpdate(target, preImage);

			Assert.That(target.Contains(TargetAttr), Is.False,
				"Existing values must not be overwritten on update.");
		}

		[Test]
		public void Update_assigns_number_when_trigger_attribute_changes_and_no_existing_value()
		{
			var auto = SeedAutoNumber(triggerEvent: 1, nextNumber: 11, digits: 3,
				prefix: "U-", suffix: "", triggerAttr: TriggerAttr);

			var target = new Entity(EntityName);
			target[TriggerAttr] = "Acme";

			RunUpdate(target, preImage: new Entity(EntityName));

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("U-011"));
			var stored = _harness.Service.GetStored("cel_autonumber", auto.Id);
			Assert.That(stored.GetAttributeValue<int>("cel_nextnumber"), Is.EqualTo(12));
		}

		[Test]
		public void Zero_digits_yields_only_prefix_and_suffix()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 99, digits: 0,
				prefix: "FOO-", suffix: "-BAR");

			var target = new Entity(EntityName);
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("FOO--BAR"));
		}

		[Test]
		public void Inactive_autonumber_records_are_ignored()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 1, digits: 2,
				prefix: "X-", suffix: "", statecode: 1);

			var target = new Entity(EntityName);
			RunCreate(target);

			Assert.That(target.Contains(TargetAttr), Is.False,
				"Deactivated cel_autonumber records (statecode != 0) are excluded from the query.");
		}

		[Test]
		public void Prefix_runtime_parameter_substitutes_attribute_value()
		{
			SeedAutoNumber(triggerEvent: 0, nextNumber: 1, digits: 3,
				prefix: "{name}-", suffix: "");

			var target = new Entity(EntityName);
			target["name"] = "ACME";
			RunCreate(target);

			Assert.That(target.GetAttributeValue<string>(TargetAttr), Is.EqualTo("ACME-001"));
		}
	}

	#endregion

	#region DeleteAutoNumber

	[TestFixture]
	public class DeleteAutoNumberTests
	{
		private const string EntityName = "account";

		private PluginHarness _harness;

		[SetUp]
		public void SetUp()
		{
			_harness = new PluginHarness
			{
				MessageName = Constants.PipelineMessage.Delete,
				Stage = Constants.PipelineStage.PreOperation,
				PrimaryEntityName = "cel_autonumber",
			};
		}

		private Guid SeedStep(string nameSuffix, string filteringAttributes = null)
		{
			var stepId = Guid.NewGuid();
			var seed = new List<object>
			{
				"sdkmessageprocessingstepid", stepId,
				"name", "CeledonPartners.AutoNumber.account" + (nameSuffix ?? ""),
			};
			if (filteringAttributes != null)
			{
				seed.Add("filteringattributes");
				seed.Add(filteringAttributes);
			}
			_harness.Service.Seed("sdkmessageprocessingstep", stepId, seed.ToArray());
			return stepId;
		}

		private Guid SeedImage(Guid stepId)
		{
			var id = Guid.NewGuid();
			_harness.Service.Seed("sdkmessageprocessingstepimage", id,
				"sdkmessageprocessingstepimageid", id,
				"sdkmessageprocessingstepid", stepId);
			return id;
		}

		private void SeedAutoNumberRecord(int triggerEvent, string targetAttr, string triggerAttr = null, string conditionalAttr = null)
		{
			var id = Guid.NewGuid();
			_harness.Service.Seed("cel_autonumber", id,
				"cel_autonumberid", id,
				"cel_entityname", EntityName,
				"cel_attributename", targetAttr,
				"cel_triggerevent", new OptionSetValue(triggerEvent),
				"cel_triggerattribute", triggerAttr ?? "",
				"cel_conditionaloptionset", conditionalAttr ?? "");
		}

		private void RunDelete(Entity preImage)
		{
			_harness.PreEntityImages["PreImage"] = preImage;
			_harness.InputParameters["Target"] = new EntityReference("cel_autonumber", preImage.Id == Guid.Empty ? Guid.NewGuid() : preImage.Id);
			new DeleteAutoNumber().Execute(_harness.Build());
		}

		[Test]
		public void Last_record_for_entity_deletes_step_and_images()
		{
			var stepId = SeedStep(" Update", filteringAttributes: "name,accountnumber");
			var imageId = SeedImage(stepId);

			var preImage = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			preImage["cel_entityname"] = EntityName;
			preImage["cel_attributename"] = "accountnumber";
			preImage["cel_triggerevent"] = new OptionSetValue(1);

			RunDelete(preImage);

			Assert.That(_harness.Service.GetStored("sdkmessageprocessingstep", stepId), Is.Null,
				"With no remaining records, the step must be deleted.");
			Assert.That(_harness.Service.GetStored("sdkmessageprocessingstepimage", imageId), Is.Null,
				"With no remaining records, the image must be deleted too.");
		}

		[Test]
		public void Update_event_keeps_step_and_recomputes_filter_when_other_records_remain()
		{
			var stepId = SeedStep(" Update", filteringAttributes: "name,accountnumber,industrycode,accountcategorycode");

			// One record being deleted, one record remaining (different attributes).
			SeedAutoNumberRecord(triggerEvent: 1, targetAttr: "accountcategorycode", triggerAttr: "industrycode");

			var preImage = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			preImage["cel_entityname"] = EntityName;
			preImage["cel_attributename"] = "accountnumber";
			preImage["cel_triggerevent"] = new OptionSetValue(1);

			RunDelete(preImage);

			var step = _harness.Service.GetStored("sdkmessageprocessingstep", stepId);
			Assert.That(step, Is.Not.Null, "Step must remain because another autonumber record still uses it.");

			var attrs = step.GetAttributeValue<string>("filteringattributes")
				.Split(',').Select(a => a.Trim()).ToList();
			Assert.That(attrs, Is.EquivalentTo(new[] { "industrycode", "accountcategorycode" }),
				"Filter must contain only attributes from the remaining records.");
		}

		[Test]
		public void Create_event_keeps_step_with_no_filter_changes_when_other_records_remain()
		{
			var stepId = SeedStep(nameSuffix: null);  // Create step has no " Update" suffix.

			SeedAutoNumberRecord(triggerEvent: 0, targetAttr: "accountcategorycode");

			var preImage = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			preImage["cel_entityname"] = EntityName;
			preImage["cel_attributename"] = "accountnumber";
			preImage["cel_triggerevent"] = new OptionSetValue(0);

			RunDelete(preImage);

			Assert.That(_harness.Service.GetStored("sdkmessageprocessingstep", stepId), Is.Not.Null);
			Assert.That(_harness.Service.UpdateCalls.Any(e => e.LogicalName == "sdkmessageprocessingstep"), Is.False,
				"Create steps don't carry filteringattributes — no update needed.");
		}

		[Test]
		public void Mismatched_event_records_do_not_keep_step_alive()
		{
			var stepId = SeedStep(" Update", filteringAttributes: "name,accountnumber");

			// Remaining record exists but for the OTHER event (Create) — must not keep this Update step alive.
			SeedAutoNumberRecord(triggerEvent: 0, targetAttr: "accountcategorycode");

			var preImage = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			preImage["cel_entityname"] = EntityName;
			preImage["cel_attributename"] = "accountnumber";
			preImage["cel_triggerevent"] = new OptionSetValue(1);

			RunDelete(preImage);

			Assert.That(_harness.Service.GetStored("sdkmessageprocessingstep", stepId), Is.Null,
				"No remaining record uses the Update step — it must be deleted.");
		}
	}

	#endregion

	#region ValidateAutoNumber

	[TestFixture]
	public class ValidateAutoNumberTests
	{
		private const string EntityName = "account";

		private PluginHarness _harness;

		[SetUp]
		public void SetUp()
		{
			_harness = new PluginHarness
			{
				MessageName = Constants.PipelineMessage.Create,
				Stage = Constants.PipelineStage.PreValidation,
				PrimaryEntityName = "cel_autonumber",
			};

			// Provide a metadata-stub that returns sensible defaults for the attribute /
			// entity metadata calls that ValidateAutoNumber issues.
			_harness.Service.ExecuteHandler = HandleMetadataRequest;
		}

		private OrganizationResponse HandleMetadataRequest(OrganizationRequest req)
		{
			switch (req)
			{
				case RetrieveEntityRequest entityReq:
					var meta = new EntityMetadata { LogicalName = entityReq.LogicalName };
					var attrs = new AttributeMetadata[]
					{
						MakeAttribute<StringAttributeMetadata>("accountnumber", AttributeTypeCode.String),
						MakeAttribute<StringAttributeMetadata>("name", AttributeTypeCode.String),
						MakeAttribute<IntegerAttributeMetadata>("revenue", AttributeTypeCode.Integer),
						MakePicklist("statuscode", new[] { 1, 2, 3 }),
					};
					typeof(EntityMetadata)
						.GetProperty(nameof(EntityMetadata.Attributes))
						.SetValue(meta, attrs);
					return new RetrieveEntityResponse { Results = { ["EntityMetadata"] = meta } };

				case RetrieveAttributeRequest attrReq:
					AttributeMetadata aMeta;
					if (attrReq.LogicalName == "statuscode") aMeta = MakePicklist("statuscode", new[] { 1, 2, 3 });
					else aMeta = MakeAttribute<StringAttributeMetadata>(attrReq.LogicalName, AttributeTypeCode.String);
					return new RetrieveAttributeResponse { Results = { ["AttributeMetadata"] = aMeta } };

				default:
					throw new NotSupportedException($"Unhandled request: {req.RequestName}");
			}
		}

		private static T MakeAttribute<T>(string logicalName, AttributeTypeCode type) where T : AttributeMetadata, new()
		{
			var meta = new T();
			typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.LogicalName)).SetValue(meta, logicalName);
			typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType)).SetValue(meta, (AttributeTypeCode?)type);
			return meta;
		}

		private static PicklistAttributeMetadata MakePicklist(string logicalName, int[] values)
		{
			var meta = new PicklistAttributeMetadata();
			typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.LogicalName)).SetValue(meta, logicalName);
			typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))
				.SetValue(meta, (AttributeTypeCode?)AttributeTypeCode.Picklist);
			var optionSet = new OptionSetMetadata();
			foreach (var v in values)
			{
				optionSet.Options.Add(new OptionMetadata(new Label(v.ToString(), 1033), v));
			}
			meta.OptionSet = optionSet;
			return meta;
		}

		private Entity NewRecord(string targetAttr = "accountnumber", string triggerAttr = null,
			string conditionalAttr = null, int conditionalValue = 0)
		{
			var t = new Entity("cel_autonumber") { Id = Guid.NewGuid() };
			t["cel_entityname"] = EntityName;
			t["cel_attributename"] = targetAttr;
			t["cel_triggerevent"] = new OptionSetValue(0);
			if (triggerAttr != null) t["cel_triggerattribute"] = triggerAttr;
			if (conditionalAttr != null)
			{
				t["cel_conditionaloptionset"] = conditionalAttr;
				t["cel_conditionalvalue"] = conditionalValue;
			}
			return t;
		}

		private void RunValidate(Entity target)
		{
			_harness.InputParameters["Target"] = target;
			new ValidateAutoNumber().Execute(_harness.Build());
		}

		[Test]
		public void Valid_record_passes_and_sets_name()
		{
			var record = NewRecord();
			RunValidate(record);

			Assert.That(record.GetAttributeValue<string>("cel_name"),
				Is.EqualTo($"AutoNumber for {EntityName}, accountnumber"));
		}

		[Test]
		public void Non_string_target_attribute_is_rejected()
		{
			var record = NewRecord(targetAttr: "revenue");
			Assert.That(() => RunValidate(record),
				Throws.TypeOf<InvalidPluginExecutionException>()
					.With.Message.Contains("text field"));
		}

		[Test]
		public void Unknown_trigger_attribute_is_rejected()
		{
			var record = NewRecord(triggerAttr: "doesnotexist");
			Assert.That(() => RunValidate(record),
				Throws.TypeOf<InvalidPluginExecutionException>()
					.With.Message.Contains("Trigger Attribute"));
		}

		[Test]
		public void Conditional_value_outside_optionset_is_rejected()
		{
			var record = NewRecord(conditionalAttr: "statuscode", conditionalValue: 999);
			Assert.That(() => RunValidate(record),
				Throws.TypeOf<InvalidPluginExecutionException>()
					.With.Message.Contains("Conditional Value"));
		}

		[Test]
		public void Duplicate_unconditional_record_is_rejected()
		{
			// Seed an existing autonumber record on the same entity+attribute, no conditional.
			var existingId = Guid.NewGuid();
			_harness.Service.Seed("cel_autonumber", existingId,
				"cel_autonumberid", existingId,
				"cel_entityname", EntityName,
				"cel_attributename", "accountnumber",
				"cel_conditionaloptionset", "",
				"cel_conditionalvalue", 0);

			var record = NewRecord();
			Assert.That(() => RunValidate(record),
				Throws.TypeOf<InvalidPluginExecutionException>()
					.With.Message.Contains("Duplicate"));
		}
	}

	#endregion
}
