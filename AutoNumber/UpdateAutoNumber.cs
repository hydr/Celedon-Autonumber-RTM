/*The MIT License (MIT)

Copyright (c) 2026 Celedon Partners

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

// Couples the plugin-step lifecycle to the cel_autonumber active state:
//   * Deactivate (statecode -> inactive) removes the steps (or recomputes the filter if other
//     active configs still share them) — so an inactive config carries no pipeline cost.
//   * Reactivate (statecode -> active) (re)registers the single + bulk steps, idempotently.
//
// This also serves as the per-record migration path: deactivating then reactivating an old config
// brings it onto the current step layout (adding the CreateMultiple/UpdateMultiple step).
//
// Registration details:
//   Message: Update, Primary Entity: cel_autonumber, Stage: Post-Operation, Mode: Synchronous
//   Filtering attributes: statecode (only status changes trigger it)
//   PreImage "Image": cel_entityname, cel_triggerevent, cel_attributename, cel_triggerattribute,
//                     cel_conditionaloptionset

using System;
using System.Collections.Generic;
using System.Linq;
using Celedon.Constants;
using Microsoft.Xrm.Sdk;

namespace Celedon
{
	public class UpdateAutoNumber : CeledonPlugin
	{
		public UpdateAutoNumber()
		{
			RegisterEvent(PipelineStage.PostOperation, PipelineMessage.Update, "cel_autonumber", Execute);
		}

		protected void Execute(LocalPluginContext context)
		{
			var target = context.GetInputParameters<UpdateInputParameters>().Target;
			if (target == null || !target.Contains("statecode"))
			{
				return;  // Only status (activate / deactivate) changes are relevant.
			}

			var isActive = target.GetAttributeValue<OptionSetValue>("statecode").Value == 0;

			// The status-only update carries just statecode; the config attributes come from the PreImage.
			var config = context.PreImage;

			if (isActive)
			{
				// Reactivated — (re)register the single + bulk steps (idempotent: merges/skips existing).
				context.Trace("cel_autonumber reactivated — registering steps.");
				CreateAutoNumber.RegisterSteps(context, config);
				return;
			}

			// Deactivated — treat like a delete for step lifecycle: keep the steps only if OTHER active
			// configs still use them (recompute the filter), otherwise remove them.
			context.Trace("cel_autonumber deactivated — removing/recomputing steps.");
			var entityName = config.GetAttributeValue<string>("cel_entityname");
			var triggerEvent = config.Contains("cel_triggerevent") && config.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value == 1 ? 1 : 0;
			var isUpdate = triggerEvent == 1;
			var currentId = config.Id;

			var remainingForEvent = context.OrganizationDataContext.CreateQuery("cel_autonumber")
																	.Where(s => s.GetAttributeValue<string>("cel_entityname").Equals(entityName)
																			 && s.GetAttributeValue<OptionSetValue>("statecode").Value == 0
																			 && s.GetAttributeValue<Guid>("cel_autonumberid") != currentId)
																	.Select(s => new {
																		TriggerEvent = s.Contains("cel_triggerevent") ? s.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value : 0,
																		TriggerAttribute = s.GetAttributeValue<string>("cel_triggerattribute"),
																		AttributeName = s.GetAttributeValue<string>("cel_attributename"),
																		ConditionalOptionSet = s.GetAttributeValue<string>("cel_conditionaloptionset")
																	})
																	.ToList()
																	.Where(s => s.TriggerEvent == triggerEvent)
																	.Select(s => new DeleteAutoNumber.ConfigFilterInfo(s.TriggerAttribute, s.AttributeName, s.ConditionalOptionSet))
																	.ToList();

			DeleteAutoNumber.RemoveOrRecomputeSteps(context, entityName, isUpdate, remainingForEvent);
		}
	}
}
