// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Http;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;
using NuGet.Protocol;

namespace Microsoft.DotNet.Cli.Build
{

    public static class ExponentialRetry
    {
        public static async System.Threading.Tasks.Task ExecuteWithRetry(Func<Task<string>> action,
            Func<string, bool> isSuccess,
            int maxRetryCount,
            Func<IEnumerable<System.Threading.Tasks.Task>> Timer,
            string taskDescription = "")
        {
            var count = 0;
            foreach (System.Threading.Tasks.Task t in Timer())
            {
                await t;
                var result = await action();
                if (isSuccess(result))
                {
                    return;
                }
                count++;
                if (count == maxRetryCount)
                {
                    throw new RetryFailedException($"Retry failed for {taskDescription} after {count} times with result: {result}");
                }
            }
            throw new Exception("Timer should not be exhausted");
        }

        public static IEnumerable<System.Threading.Tasks.Task> Timer(IEnumerable<TimeSpan> interval)
        {
            foreach (var i in interval)
            {
                var task = System.Threading.Tasks.Task.Delay(i);
                yield return task;
            }
        }

        public static IEnumerable<TimeSpan> Intervals
        {
            get
            {
                int seconds = 5;
                while (true)
                {
                    yield return TimeSpan.FromSeconds(seconds);
                    seconds *= 2;
                }
            }
        }
    }
}
