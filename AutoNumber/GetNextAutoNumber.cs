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

// Gets the next available number and adds it to the Target

using System;
using System.Collections.Generic;
using System.Linq;
using Celedon.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Celedon
{
    public class GetNextAutoNumber : CeledonPlugin
    {
        //
        // This is the main plugin that creates the numbers and adds them to new records
        // This plugin is not registered by default.  It is registered and unregistered dynamically by the CreateAutoNumber and DeleteAutoNumber plugins respectively
        //
        public GetNextAutoNumber(string pluginConfig)
        {
            // Need to support older version
            if (pluginConfig.TryParseJson(out AutoNumberPluginConfig config))
            {
                RegisterEvent(PipelineStage.PreOperation, config.EventName, config.EntityName, Execute);
                // Also handle the bulk message (CreateMultiple / UpdateMultiple) so 100 records in one
                // request are numbered in a single invocation instead of fanning out per record.
                RegisterEvent(PipelineStage.PreOperation, config.EventName + "Multiple", config.EntityName, Execute);
            }
            else
            {
                RegisterEvent(PipelineStage.PreOperation, PipelineMessage.Create, pluginConfig, Execute);
                RegisterEvent(PipelineStage.PreOperation, PipelineMessage.CreateMultiple, pluginConfig, Execute);
            }
        }

        // One record from the (single or bulk) operation, paired with its pre-image (Update only).
        private struct TargetItem
        {
            public Entity Target;
            public Entity PreImage;
            public TargetItem(Entity target, Entity preImage) { Target = target; PreImage = preImage; }
        }

        protected void Execute(LocalPluginContext context)
        {
            var message = context.PluginExecutionContext.MessageName;
            var isUpdate = message.StartsWith(PipelineMessage.Update, StringComparison.OrdinalIgnoreCase);  // Update or UpdateMultiple

            #region Gather the batch of target records (single Target, or bulk Targets) with their pre-images

            var batch = GetTargets(context);
            if (batch.Count == 0)
            {
                return;
            }

            #endregion

            #region Get the list of autonumber records applicable to the target entity type

            var autoNumberIdList = context.OrganizationDataContext.CreateQuery("cel_autonumber")
                                                                  .Where(a => a.GetAttributeValue<string>("cel_entityname").Equals(context.PluginExecutionContext.PrimaryEntityName) && a.GetAttributeValue<OptionSetValue>("statecode").Value == 0 && a.GetAttributeValue<OptionSetValue>("cel_triggerevent").Value == (isUpdate ? 1 : 0))
                                                                  .OrderBy(a => a.GetAttributeValue<Guid>("cel_autonumberid"))  // Insure they are ordered, to prevent deadlocks
                                                                  .Select(a => a.GetAttributeValue<Guid>("cel_autonumberid"))
                                                                  .ToList();
            #endregion

            #region This loop locks the autonumber record(s) so only THIS transaction can read/write it

            foreach (var autoNumberId in autoNumberIdList)
            {
                var lockingUpdate = new Entity("cel_autonumber")
                {
                    Id = autoNumberId,
                    ["cel_preview"] = "555"
                };
                // Use the preview field as our "dummy" field - so we don't need a dedicated "dummy"

                context.OrganizationService.Update(lockingUpdate);
            }

            #endregion

            #region For each config, reserve a contiguous number block for the whole batch (one increment)

            foreach (var autoNumberId in autoNumberIdList)
            {
                var autoNumber = context.OrganizationService.Retrieve("cel_autonumber", autoNumberId, new ColumnSet(
                    "cel_attributename",
                    "cel_triggerattribute",
                    "cel_conditionaloptionset",
                    "cel_conditionalvalue",
                    "cel_digits",
                    "cel_prefix",
                    "cel_nextnumber",
                    "cel_suffix"));

                var targetAttribute = autoNumber.GetAttributeValue<string>("cel_attributename");
                var nextNumber = autoNumber.GetAttributeValue<int>("cel_nextnumber");
                var startNumber = nextNumber;
                string lastGenerated = null;

                foreach (var item in batch)
                {
                    var target = item.Target;

                    #region Check conditions that prevent creating an autonumber (per record)

                    if (isUpdate && !target.Contains(autoNumber.GetAttributeValue<string>("cel_triggerattribute")))
                    {
                        continue;  // Continue, if this is an Update event and the target does not contain the trigger value
                    }
                    else if ((autoNumber.Contains("cel_conditionaloptionset") && (!target.Contains(autoNumber.GetAttributeValue<string>("cel_conditionaloptionset")) || target.GetAttributeValue<OptionSetValue>(autoNumber.GetAttributeValue<string>("cel_conditionaloptionset")).Value != autoNumber.GetAttributeValue<int>("cel_conditionalvalue"))))
                    {
                        continue;  // Continue, if this is a conditional optionset
                    }
                    else if (target.Contains(targetAttribute) && !string.IsNullOrWhiteSpace(target.GetAttributeValue<string>(targetAttribute)))
                    {
                        continue;  // Continue so we don't overwrite a manual value
                    }
                    else if (isUpdate && item.PreImage != null && item.PreImage.Contains(targetAttribute) && !string.IsNullOrWhiteSpace(item.PreImage.GetAttributeValue<string>(targetAttribute)))
                    {
                        context.TracingService.Trace("Target attribute '{0}' is already populated. Continue, so we don't overwrite an existing value.", targetAttribute);
                        continue;  // Continue, so we don't overwrite an existing value
                    }
                    #endregion

                    // Generate number from the reserved block and insert into the target record.
                    target[targetAttribute] = FormatAutoNumber(context.OrganizationService, autoNumber, nextNumber, target);
                    lastGenerated = target.GetAttributeValue<string>(targetAttribute);
                    nextNumber++;
                }

                // Single increment for the whole batch (only if at least one number was assigned).
                if (nextNumber != startNumber)
                {
                    context.OrganizationService.Update(new Entity("cel_autonumber")
                    {
                        Id = autoNumber.Id,
                        ["cel_nextnumber"] = nextNumber,
                        ["cel_preview"] = lastGenerated
                    });
                }
            }

            #endregion
        }

        // Collects the records to process: a bulk EntityCollection ("Targets" for CreateMultiple/UpdateMultiple),
        // otherwise the single "Target".  Pre-images are paired per record (collection for bulk, singular otherwise).
        private static List<TargetItem> GetTargets(LocalPluginContext context)
        {
            var ctx = context.PluginExecutionContext;
            var items = new List<TargetItem>();

            if (ctx.InputParameters.Contains("Targets") && ctx.InputParameters["Targets"] is EntityCollection targets)
            {
                var preImages = GetPreImagesCollection(ctx);

                for (var i = 0; i < targets.Entities.Count; i++)
                {
                    Entity pre = null;
                    if (preImages != null && i < preImages.Length && preImages[i] != null && preImages[i].Values.Count > 0)
                    {
                        pre = preImages[i].Values.First();
                    }
                    items.Add(new TargetItem(targets.Entities[i], pre));
                }
            }
            else if (ctx.InputParameters.Contains("Target") && ctx.InputParameters["Target"] is Entity target)
            {
                Entity pre = null;
                if (ctx.PreEntityImages != null && ctx.PreEntityImages.Values.Count > 0)
                {
                    pre = ctx.PreEntityImages.Values.First();
                }
                items.Add(new TargetItem(target, pre));
            }

            return items;
        }

        // PreEntityImagesCollection (the per-record pre-images for UpdateMultiple) is not on the
        // IPluginExecutionContext interface in this SDK version, but the runtime context object exposes
        // it.  Read it via reflection so we don't have to upgrade the SDK package.  Returns null when
        // unavailable (e.g. CreateMultiple, which has no pre-images anyway).
        private static EntityImageCollection[] GetPreImagesCollection(IPluginExecutionContext ctx)
        {
            try
            {
                var prop = ctx.GetType().GetProperty("PreEntityImagesCollection");
                return prop?.GetValue(ctx) as EntityImageCollection[];
            }
            catch
            {
                return null;
            }
        }

        // Pure number formatting: prefix + zero-padded numberValue + suffix, with {token} replacement
        // resolved against parameterContext.  No database access.
        internal static string FormatAutoNumber(IOrganizationService service, Entity autoNumberConfig, int numberValue, Entity parameterContext)
        {
            var numDigits = autoNumberConfig.GetAttributeValue<int>("cel_digits");
            var prefix = service.ReplaceParameters(parameterContext, autoNumberConfig.GetAttributeValue<string>("cel_prefix"));
            var number = numDigits == 0 ? "" : numberValue.ToString("D" + numDigits);
            var postfix = service.ReplaceParameters(parameterContext, autoNumberConfig.GetAttributeValue<string>("cel_suffix"));
            return $"{prefix}{number}{postfix}";
        }

        /// <summary>
        /// Locks the cel_autonumber config record (via cel_preview), builds the formatted number,
        /// increments cel_nextnumber by one, and returns the result.  Single-record helper used by the
        /// on-demand action.  Contains NO pipeline guards — callers apply whatever guards they require.
        /// </summary>
        internal static string GenerateNumber(IOrganizationService service, ITracingService tracing, Entity autoNumberConfig, Entity parameterContext)
        {
            // Lock the config row so only this transaction can read/write the counter.
            service.Update(new Entity("cel_autonumber") { Id = autoNumberConfig.Id, ["cel_preview"] = "555" });

            // Re-read the counter AFTER acquiring the lock.  The passed config may have been retrieved
            // BEFORE the lock (the on-demand action retrieves it in ResolveConfig); under READ COMMITTED
            // SNAPSHOT a pre-lock value could hand the same number to two concurrent callers.
            var nextNumber = service.Retrieve("cel_autonumber", autoNumberConfig.Id, new ColumnSet("cel_nextnumber"))
                                    .GetAttributeValue<int>("cel_nextnumber");
            var result = FormatAutoNumber(service, autoNumberConfig, nextNumber, parameterContext);
            tracing?.Trace("Generated autonumber '{0}' from config {1}", result, autoNumberConfig.Id);

            // Increment next number in db
            service.Update(new Entity("cel_autonumber")
            {
                Id = autoNumberConfig.Id,
                ["cel_nextnumber"] = nextNumber + 1,
                ["cel_preview"] = result
            });

            return result;
        }
    }
}
