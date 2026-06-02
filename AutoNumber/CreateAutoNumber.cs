/*The MIT License (MIT)

Copyright (c) 2015 Celedon Partners 

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

// Generates a plugin step for an entity, when a new autonumber record is created

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Celedon.Constants;

namespace Celedon
{
	public class CreateAutoNumber : CeledonPlugin
	{
		//
		// This plugin is executed when a new AutoNumber record is created.  It generates the plugin steps on the entity type to create each number
		//
		// Registration Details:
		// Message: Create
		// Primary Entity: cel_autonumber
		// User context: SYSTEM
		// Event Pipeline: Post
		// Mode: Async
		// Config: none
		//

		internal const string PluginName = "CeledonPartners.AutoNumber.{0}";

		public CreateAutoNumber()
		{
			RegisterEvent(PipelineStage.PostOperation, PipelineMessage.Create, "cel_autonumber", Execute);
		}

		protected void Execute(LocalPluginContext context)
		{
		    context.Trace("Get Target record");
			var target = context.GetInputParameters<CreateInputParameters>().Target;
			var entityName = target.GetAttributeValue<string>("cel_entityname");
			var isUpdate = target.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value == 1;
			var eventName = isUpdate ? PipelineMessage.Update : PipelineMessage.Create;
			var targetAttribute = target.GetAttributeValue<string>("cel_attributename");
			var filterAttrs = BuildFilterAttributes(target);

		    context.Trace("Get the Id of this plugin");
		    var pluginTypeId = context.OrganizationDataContext.CreateQuery("plugintype")
				 											   .Where(s => s.GetAttributeValue<string>("name").Equals(typeof(GetNextAutoNumber).FullName))
															   .Select(s => s.GetAttributeValue<Guid>("plugintypeid"))
															   .First();

			// Register/merge the single-record step (Create / Update) ...
			RegisterOrMergeStep(context, entityName, eventName, StepName(entityName, isUpdate, null),
				isUpdate, targetAttribute, filterAttrs, pluginTypeId, required: true);

			// ... and the bulk step (CreateMultiple / UpdateMultiple) so 100-record requests are numbered
			// in one invocation.  Skipped (not fatal) if the entity has no *Multiple message filter.
			var bulkMessage = eventName + "Multiple";
			RegisterOrMergeStep(context, entityName, bulkMessage, StepName(entityName, isUpdate, bulkMessage),
				isUpdate, targetAttribute, filterAttrs, pluginTypeId, required: false);
		}

		// Step name scheme — single steps keep their historical names; bulk steps get a " (Message)" suffix.
		internal static string StepName(string entityName, bool isUpdate, string bulkMessage)
		{
			var name = string.Format(PluginName, entityName);
			if (isUpdate) { name += " Update"; }
			if (!string.IsNullOrEmpty(bulkMessage)) { name += " (" + bulkMessage + ")"; }
			return name;
		}

		private void RegisterOrMergeStep(LocalPluginContext context, string entityName, string messageName,
			string stepName, bool isUpdate, string targetAttribute, HashSet<string> filterAttrs, Guid pluginTypeId, bool required)
		{
			context.Trace("Check for existing plugin step: " + stepName);
			var existingStep = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstep")
				.Where(s => s.GetAttributeValue<string>("name").Equals(stepName))
				.Select(s => new { Id = s.GetAttributeValue<Guid>("sdkmessageprocessingstepid"),
								   Filter = s.GetAttributeValue<string>("filteringattributes") })
				.ToList()
				.FirstOrDefault();

			if (existingStep != null)
			{
				if (!isUpdate)
				{
					return;  // Create-Step has no filtering attributes to merge.
				}

				var merged = new HashSet<string>(
					(existingStep.Filter ?? string.Empty).Split(',').Select(a => a.Trim()),
					StringComparer.OrdinalIgnoreCase);
				merged.UnionWith(filterAttrs);
				merged.RemoveWhere(string.IsNullOrWhiteSpace);

				context.Trace("Merge filteringattributes into existing plugin step");
				context.OrganizationService.Update(new Entity("sdkmessageprocessingstep", existingStep.Id)
				{
					["filteringattributes"] = string.Join(",", merged)
				});
				return;
			}

			var config = new AutoNumberPluginConfig() { EntityName = entityName, EventName = isUpdate ? "Update" : "Create" };

		    context.Trace("Get the message id from this org: " + messageName);
		    var messageId = context.OrganizationDataContext.CreateQuery("sdkmessage")
															.Where(s => s.GetAttributeValue<string>("name").Equals(messageName))
															.Select(s => s.GetAttributeValue<Guid>("sdkmessageid"))
															.ToList()
															.FirstOrDefault();
			if (messageId == Guid.Empty)
			{
				if (required) { throw new InvalidPluginExecutionException($"SDK message '{messageName}' not found."); }
				context.Trace($"Message '{messageName}' not found; skipping bulk step.");
				return;
			}

		    context.Trace("Get the filterId for the specific entity from this org");
			var filterId = context.OrganizationDataContext.CreateQuery("sdkmessagefilter")
														   .Where(s => s.GetAttributeValue<string>("primaryobjecttypecode").Equals(entityName)
															   && s.GetAttributeValue<EntityReference>("sdkmessageid").Id.Equals(messageId))
														   .Select(s => s.GetAttributeValue<Guid>("sdkmessagefilterid"))
														   .ToList()
														   .FirstOrDefault();
			if (filterId == Guid.Empty)
			{
				if (required) { throw new InvalidPluginExecutionException($"No message filter for '{messageName}' on '{entityName}'."); }
				context.Trace($"No '{messageName}' filter for '{entityName}'; skipping bulk step (entity may not support it).");
				return;
			}

		    context.Trace("Build new plugin step");
			var stepAttributes = new AttributeCollection()
			{
				{ "name", stepName },
				{ "description", stepName },
				{ "plugintypeid", pluginTypeId.ToEntityReference("plugintype") },  // This plugin type
				{ "sdkmessageid", messageId.ToEntityReference("sdkmessage") },  // Create(Multiple) or Update(Multiple) Message
				{ "configuration", config.ToJson() },  // EntityName and RegisteredEvent in the UnsecureConfig
				{ "stage", PipelineStage.PreOperation.ToOptionSetValue() },  // Execution Stage: Pre-Operation
				{ "rank", 1 },
				{ "impersonatinguserid", context.PluginExecutionContext.UserId.ToEntityReference("systemuser") },  // Run as SYSTEM user. Assumes we are currently running as the SYSTEM user
				{ "sdkmessagefilterid", filterId.ToEntityReference("sdkmessagefilter") },
			};

			if (isUpdate && filterAttrs.Count > 0)
			{
				// Scope the Update step to changes that the runtime checks in GetNextAutoNumber.Execute actually inspect,
				// so the pipeline does not load the plugin on unrelated attribute changes.
				stepAttributes.Add("filteringattributes", string.Join(",", filterAttrs));
			}

			var newPluginStep = new Entity("sdkmessageprocessingstep") { Attributes = stepAttributes };

		    context.Trace("Create new plugin step");
		    var sdkmessageprocessingstepid = context.OrganizationService.Create(newPluginStep);

            // only add the image if the type is update, on create a value cannot be overridden
		    if (isUpdate)
		    {
				// Bulk messages carry the records in "Targets"; single messages in "Target".
				var messageProperty = messageName.EndsWith("Multiple") ? "Targets" : "Target";
		        context.Trace("Build new plugin step image");
		        var newPluginStepImage = new Entity("sdkmessageprocessingstepimage")
		        {
		            Attributes = new AttributeCollection()
		            {
		                {"sdkmessageprocessingstepid", sdkmessageprocessingstepid.ToEntityReference("sdkmessageprocessingstep")},
		                {"imagetype", 0.ToOptionSetValue()}, // PreImage
		                {"messagepropertyname", messageProperty},
		                {"name", "Image"},
		                {"entityalias", "Image"},
		                {"attributes", targetAttribute}, //Only incluce the one attribute we really need.
		            }
		        };

		        context.Trace("Create new plugin step image");
		        context.OrganizationService.Create(newPluginStepImage);
		    }
		}

		internal static HashSet<string> BuildFilterAttributes(Entity autoNumberRecord)
		{
			var attrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var trigger = autoNumberRecord.GetAttributeValue<string>("cel_triggerattribute");
			if (!string.IsNullOrWhiteSpace(trigger))
			{
				attrs.Add(trigger);
			}

			var targetAttr = autoNumberRecord.GetAttributeValue<string>("cel_attributename");
			if (!string.IsNullOrWhiteSpace(targetAttr))
			{
				attrs.Add(targetAttr);
			}

			var conditional = autoNumberRecord.GetAttributeValue<string>("cel_conditionaloptionset");
			if (!string.IsNullOrWhiteSpace(conditional))
			{
				attrs.Add(conditional);
			}

			return attrs;
		}
	}
}
