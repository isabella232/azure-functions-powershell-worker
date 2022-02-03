﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

namespace Microsoft.Azure.Functions.PowerShellWorker.Durable
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Management.Automation;

    // using PowerShellWorker.Utility;
    using Microsoft.Azure.Functions.PowerShellWorker.Durable.Actions;

    internal class OrchestrationInvoker : IOrchestrationInvoker
    {
        private Action<PowerShell> externalInvoker = null;

        public Hashtable Invoke(OrchestrationBindingInfo orchestrationBindingInfo, IPowerShellServices pwsh)
        {
            try
            {
                var outputBuffer = new PSDataCollection<object>();
                var context = orchestrationBindingInfo.Context;

                // context.History should never be null when initializing CurrentUtcDateTime
                var orchestrationStart = context.History.First(
                    e => e.EventType == HistoryEventType.OrchestratorStarted);
                context.CurrentUtcDateTime = orchestrationStart.Timestamp.ToUniversalTime();

                // Marks the first OrchestratorStarted event as processed
                orchestrationStart.IsProcessed = true;

                var useExternalSDK = externalInvoker != null;
                if (useExternalSDK)
                {
                    externalInvoker.Invoke(pwsh.GetPowerShell());
                    var result = orchestrationBindingInfo.Context.ExternalResult;
                    var isError = orchestrationBindingInfo.Context.ExternalIsError;
                    if (isError)
                    {
                        throw (Exception)result;
                    }
                    else
                    {
                        return (Hashtable)result;
                    }
                }
                
                var asyncResult = pwsh.BeginInvoke(outputBuffer);

                var (shouldStop, actions) =
                    orchestrationBindingInfo.Context.OrchestrationActionCollector.WaitForActions(asyncResult.AsyncWaitHandle);

                if (shouldStop)
                {
                    // The orchestration function should be stopped and restarted
                    pwsh.StopInvoke();
                    // return (Hashtable)orchestrationBindingInfo.Context.OrchestrationActionCollector.output;
                    return CreateOrchestrationResult(isDone: false, actions, output: null, context.CustomStatus);
                }
                else
                {
                    try
                    {
                        // The orchestration function completed
                        pwsh.EndInvoke(asyncResult);
                        var result = CreateReturnValueFromFunctionOutput(outputBuffer);
                        return CreateOrchestrationResult(isDone: true, actions, output: result, context.CustomStatus);
                    }
                    catch (Exception e)
                    {
                        // The orchestrator code has thrown an unhandled exception:
                        // this should be treated as an entire orchestration failure
                        throw new OrchestrationFailureException(actions, context.CustomStatus, e);
                    }
                }
            }
            finally
            {
                pwsh.ClearStreamsAndCommands();
            }
        }

        public static object CreateReturnValueFromFunctionOutput(IList<object> pipelineItems)
        {
            if (pipelineItems == null || pipelineItems.Count <= 0)
            {
                return null;
            }

            return pipelineItems.Count == 1 ? pipelineItems[0] : pipelineItems.ToArray();
        }

        private static Hashtable CreateOrchestrationResult(
            bool isDone,
            List<List<OrchestrationAction>> actions,
            object output,
            object customStatus)
        {
            var orchestrationMessage = new OrchestrationMessage(isDone, actions, output, customStatus);
            return new Hashtable { { "$return", orchestrationMessage } };
        }

        public void SetExternalInvoker(Action<PowerShell> externalInvoker)
        {
            this.externalInvoker = externalInvoker;
        }
    }
}