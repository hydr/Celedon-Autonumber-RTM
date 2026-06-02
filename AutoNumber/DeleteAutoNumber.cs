/*The MIT License (MIT)

Copyright (c) 2017 Celedon Partners 

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

// Removes the plugin step from an entity, if there are no registered autonumber records

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;

namespace Celedon
{
	public class DeleteAutoNumber : CeledonPlugin
	{
		//
		// This plugin is executed when an AutoNumber record is deleted, it will remove the plugin steps from the associated entity
		//
		// Registration details:
		// Message: Delete
		// Primary Entity: cel_autonumber
		// User context: SYSTEM
		// Event Pipeline: Post
		// Mode: Async
		// Config: none
		//
		// PreImage:
		// Name: PreImage
		// Alias: PreImage
		// Attributes: cel_entityname, cel_attributename
		//
		public DeleteAutoNumber()
		{
			//this.RegisteredEvents.Add(new Tuple<int,string,string,Action<LocalPluginContext>>(PostOperation, DELETEMESSAGE, "entityname", new Action<LocalPluginContext>(Execute)));
			// PostOperation: the deleted record must already be gone so the "remaining records" query
			// is accurate, and it must match the registered step's stage (40) or Execute never fires.
			RegisterEvent(Constants.PipelineStage.PostOperation, Constants.PipelineMessage.Delete, "cel_autonumber", Execute);
		}

		protected void Execute(LocalPluginContext context)
		{
			var entityName = context.PreImage.GetAttributeValue<string>("cel_entityname");
			var triggerEvent = context.PreImage.Contains("cel_triggerevent") && context.PreImage.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value == 1 ? 1 : 0;
			var isUpdate = triggerEvent == 1;

			// Post-Delete: the record is already gone, so "remaining" naturally excludes it.
			var remainingForEvent = context.OrganizationDataContext.CreateQuery("cel_autonumber")
																	.Where(s => s.GetAttributeValue<string>("cel_entityname").Equals(entityName))
																	.Select(s => new {
																		TriggerEvent = s.Contains("cel_triggerevent") ? s.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value : 0,
																		TriggerAttribute = s.GetAttributeValue<string>("cel_triggerattribute"),
																		AttributeName = s.GetAttributeValue<string>("cel_attributename"),
																		ConditionalOptionSet = s.GetAttributeValue<string>("cel_conditionaloptionset")
																	})
																	.ToList()
																	.Where(s => s.TriggerEvent == triggerEvent)
																	.Select(s => new ConfigFilterInfo(s.TriggerAttribute, s.AttributeName, s.ConditionalOptionSet))
																	.ToList();

			RemoveOrRecomputeSteps(context, entityName, isUpdate, remainingForEvent);
		}

		// Filter-relevant attributes of a cel_autonumber config that remains for an entity/event.
		internal struct ConfigFilterInfo
		{
			public string TriggerAttribute;
			public string AttributeName;
			public string ConditionalOptionSet;
			public ConfigFilterInfo(string trigger, string attribute, string conditional)
			{
				TriggerAttribute = trigger; AttributeName = attribute; ConditionalOptionSet = conditional;
			}
		}

		// Given the configs that REMAIN for this entity/event, either recompute the steps' filter (Update,
		// when others remain) or delete both the single and bulk steps (when none remain).  Reused by
		// DeleteAutoNumber (on delete) and UpdateAutoNumber (on deactivate).
		internal static void RemoveOrRecomputeSteps(LocalPluginContext context, string entityName, bool isUpdate, System.Collections.Generic.List<ConfigFilterInfo> remainingForEvent)
		{
			var eventName = isUpdate ? "Update" : "Create";
			var stepNames = new[]
			{
				CreateAutoNumber.StepName(entityName, isUpdate, null),
				CreateAutoNumber.StepName(entityName, isUpdate, eventName + "Multiple")
			};

			if (remainingForEvent.Any())  // Other autonumber records still use these steps — keep them, recompute the Update filter.
			{
				if (isUpdate)
				{
					var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (var record in remainingForEvent)
					{
						if (!string.IsNullOrWhiteSpace(record.TriggerAttribute)) rebuilt.Add(record.TriggerAttribute);
						if (!string.IsNullOrWhiteSpace(record.AttributeName)) rebuilt.Add(record.AttributeName);
						if (!string.IsNullOrWhiteSpace(record.ConditionalOptionSet)) rebuilt.Add(record.ConditionalOptionSet);
					}

					var filter = string.Join(",", rebuilt);
					foreach (var name in stepNames) UpdateStepFilter(context, name, filter);
				}

				return;
			}

			// No records remain for this event — remove both steps (and their images).
			foreach (var name in stepNames) DeleteStepByName(context, name);
		}

		private static void UpdateStepFilter(LocalPluginContext context, string stepName, string filter)
		{
			var stepId = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstep")
														.Where(s => s.GetAttributeValue<string>("name").Equals(stepName))
														.Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid"))
														.ToList()
														.FirstOrDefault();
			if (stepId != Guid.Empty)
			{
				context.OrganizationService.Update(new Entity("sdkmessageprocessingstep", stepId)
				{
					["filteringattributes"] = filter
				});
			}
		}

		private static void DeleteStepByName(LocalPluginContext context, string stepName)
		{
			var stepId = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstep")
														.Where(s => s.GetAttributeValue<string>("name").Equals(stepName))
														.Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid"))
														.ToList()
														.FirstOrDefault();
			if (stepId == Guid.Empty)
			{
				return;  // Step doesn't exist (e.g. entity had no *Multiple filter) — nothing to do.
			}

			// Delete all images first
			var images = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstepimage")
				.Where(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid").Equals(stepId))
				.Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepimageid"))
				.ToList();

			foreach (var image in images)
			{
				context.OrganizationService.Delete("sdkmessageprocessingstepimage", image);
			}

			context.OrganizationService.Delete("sdkmessageprocessingstep", stepId);
		}
	}
}
