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

// Handler for the global custom action cel_GenerateAutoNumber.
// Assigns an autonumber to a record ON DEMAND (from a classic workflow, Power Automate, or code),
// bypassing the regular trigger condition (conditional optionset / status / trigger attribute)
// but NOT overwriting a value that is already present on the target field.

using System;
using System.Collections.Generic;
using System.Linq;
using Celedon.Constants;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Celedon
{
	public class GenerateAutoNumberAction : CeledonPlugin
	{
		public const string MessageName = "cel_GenerateAutoNumber";
		public const string InTarget    = "Target";           // EntityReference (required)
		public const string InConfig    = "AutoNumberConfig"; // EntityReference to cel_autonumber (optional)
		public const string InAttribute = "AttributeName";    // String (fallback when no config given)
		public const string OutNumber   = "Number";           // String (output)

		public GenerateAutoNumberAction()
		{
			// Global custom action: empty entity name => wildcard match in CeledonPlugin.Execute.
			// Stage 30 (Main/Core Operation) runs inside the platform transaction, so the
			// cel_preview lock + counter increment stay serialized against concurrent callers.
			RegisterEvent(PipelineStage.MainOperation, MessageName, "", Execute);
		}

		protected void Execute(LocalPluginContext context)
		{
			var input = context.PluginExecutionContext.InputParameters;
			var service = context.OrganizationService;

			if (!input.TryGetValueNotNull(InTarget, out EntityReference target))
			{
				throw new InvalidPluginExecutionException($"{MessageName}: '{InTarget}' (EntityReference) is required.");
			}

			var config = ResolveConfig(context, target);
			var targetAttribute = config.GetAttributeValue<string>("cel_attributename");

			// Retrieve the target record: the target attribute (for the overwrite check) plus any
			// columns referenced by {tokens} in the prefix/suffix.
			var record = service.Retrieve(target.LogicalName, target.Id, BuildContextColumns(config, targetAttribute));

			// Don't overwrite an existing number — return what is already there.
			if (record.Contains(targetAttribute) && !string.IsNullOrWhiteSpace(record.GetAttributeValue<string>(targetAttribute)))
			{
				context.TracingService.Trace("{0}: '{1}.{2}' already populated; not overwriting.", MessageName, target.LogicalName, targetAttribute);
				context.PluginExecutionContext.OutputParameters[OutNumber] = record.GetAttributeValue<string>(targetAttribute);
				return;
			}

			var number = GetNextAutoNumber.GenerateNumber(service, context.TracingService, config, record);

			service.Update(new Entity(target.LogicalName) { Id = target.Id, [targetAttribute] = number });

			context.PluginExecutionContext.OutputParameters[OutNumber] = number;
		}

		// Identify the cel_autonumber config: by explicit reference, else by Target entity + attribute name.
		private static Entity ResolveConfig(LocalPluginContext context, EntityReference target)
		{
			var input = context.PluginExecutionContext.InputParameters;
			var service = context.OrganizationService;

			if (input.TryGetValueNotNull(InConfig, out EntityReference configRef))
			{
				var config = service.Retrieve("cel_autonumber", configRef.Id, AutoNumberColumns());
				var configEntity = config.GetAttributeValue<string>("cel_entityname");
				if (!string.Equals(configEntity, target.LogicalName, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidPluginExecutionException(
						$"{MessageName}: config targets '{configEntity}' but Target is '{target.LogicalName}'.");
				}
				return config;
			}

			if (!input.TryGetValueNotNull(InAttribute, out string attributeName) || string.IsNullOrWhiteSpace(attributeName))
			{
				throw new InvalidPluginExecutionException($"{MessageName}: provide '{InConfig}' or '{InAttribute}'.");
			}

			var matches = context.OrganizationDataContext.CreateQuery("cel_autonumber")
				.Where(a => a.GetAttributeValue<string>("cel_entityname") == target.LogicalName
						 && a.GetAttributeValue<string>("cel_attributename") == attributeName
						 && a.GetAttributeValue<OptionSetValue>("statecode").Value == 0)
				.OrderBy(a => a.GetAttributeValue<Guid>("cel_autonumberid"))
				.Select(a => a.GetAttributeValue<Guid>("cel_autonumberid"))
				.ToList();

			if (matches.Count == 0)
			{
				throw new InvalidPluginExecutionException(
					$"{MessageName}: no active cel_autonumber for {target.LogicalName}.{attributeName}.");
			}
			if (matches.Count > 1)
			{
				throw new InvalidPluginExecutionException(
					$"{MessageName}: multiple active cel_autonumber records for {target.LogicalName}.{attributeName}. Pass '{InConfig}' to disambiguate.");
			}

			return service.Retrieve("cel_autonumber", matches[0], AutoNumberColumns());
		}

		private static ColumnSet AutoNumberColumns()
		{
			return new ColumnSet("cel_entityname", "cel_attributename", "cel_digits", "cel_prefix", "cel_nextnumber", "cel_suffix");
		}

		// Target columns needed: the target attribute (overwrite check) + attributes referenced by
		// {tokens} in prefix/suffix (parent-lookup tokens contribute the lookup column on the target,
		// which ReplaceParameters follows to the parent record).
		private static ColumnSet BuildContextColumns(Entity config, string targetAttribute)
		{
			var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetAttribute };

			foreach (var template in new[] { config.GetAttributeValue<string>("cel_prefix"), config.GetAttributeValue<string>("cel_suffix") })
			{
				if (string.IsNullOrWhiteSpace(template))
				{
					continue;
				}

				foreach (var param in RuntimeParameter.GetParametersFromString(template))
				{
					if (param.IsRandomParameter())
					{
						continue;
					}

					columns.Add(param.IsParentParameter() ? param.ParentLookupName : param.AttributeName);
				}
			}

			return new ColumnSet(columns.ToArray());
		}
	}
}
