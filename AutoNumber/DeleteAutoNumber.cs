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
			var triggerEvent = context.PreImage.Contains("cel_triggerevent") && context.PreImage.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value == 1 ? 1 : 0;

			var remainingAutoNumberList = context.OrganizationDataContext.CreateQuery("cel_autonumber")
																		 .Where(s => s.GetAttributeValue<string>("cel_entityname").Equals(context.PreImage.GetAttributeValue<string>("cel_entityname")))
																		 .Select(s => new {
																			 Id = s.GetAttributeValue<Guid>("cel_autonumberid"),
																			 TriggerEvent = s.Contains("cel_triggerevent") ? s.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value : 0,
																			 TriggerAttribute = s.GetAttributeValue<string>("cel_triggerattribute"),
																			 AttributeName = s.GetAttributeValue<string>("cel_attributename"),
																			 ConditionalOptionSet = s.GetAttributeValue<string>("cel_conditionaloptionset")
																		 })
																		 .ToList();

			var pluginName = string.Format(CreateAutoNumber.PluginName, context.PreImage.GetAttributeValue<string>("cel_entityname"));
			if (triggerEvent == 1)
			{
				pluginName += " Update";
			}

			var remainingForEvent = remainingAutoNumberList.Where(s => s.TriggerEvent == triggerEvent).ToList();

			if (remainingForEvent.Any())  // If there are still other autonumber records on this entity, keep the step but recompute its filter for Update.
			{
				if (triggerEvent == 1)
				{
					var rebuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (var record in remainingForEvent)
					{
						if (!string.IsNullOrWhiteSpace(record.TriggerAttribute)) rebuilt.Add(record.TriggerAttribute);
						if (!string.IsNullOrWhiteSpace(record.AttributeName)) rebuilt.Add(record.AttributeName);
						if (!string.IsNullOrWhiteSpace(record.ConditionalOptionSet)) rebuilt.Add(record.ConditionalOptionSet);
					}

					var stepId = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstep")
																.Where(s => s.GetAttributeValue<string>("name").Equals(pluginName))
																.Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid"))
																.ToList()
																.FirstOrDefault();

					if (stepId != Guid.Empty)
					{
						context.OrganizationService.Update(new Entity("sdkmessageprocessingstep", stepId)
						{
							["filteringattributes"] = string.Join(",", rebuilt)
						});
					}
				}

				return;
			}

			var pluginStepList = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstep")
																.Where(s => s.GetAttributeValue<string>("name").Equals(pluginName))
																.Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid"))
																.ToList();

			if (!pluginStepList.Any())  // Plugin is already deleted, nothing to do here.
			{
				return;  
			}

            // Delete all images
		    var images = context.OrganizationDataContext.CreateQuery("sdkmessageprocessingstepimage")
		        .Where(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepid").Equals(pluginStepList.First()))
		        .Select(s => s.GetAttributeValue<Guid>("sdkmessageprocessingstepimageid"))
		        .ToList();

		    foreach (var image in images)
		    {
		        context.OrganizationService.Delete("sdkmessageprocessingstepimage", image);
            }

            // Delete plugin step
            context.OrganizationService.Delete("sdkmessageprocessingstep", pluginStepList.First());
		}
	}
}
