﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NeoSharp.Core.IntervalScheduler
{
    public abstract class IntervalScheduler
    {
        private static readonly List<CancellationTokenSource> _cancellationTokens = new List<CancellationTokenSource>();

        public static async Task Run(TimeSpan interval, CancellationTokenSource cancellationToken, Func<Task> action)
        {
            if (cancellationToken == null)
            {
                throw new ArgumentNullException(nameof(cancellationToken));
            }
            
            if (!_cancellationTokens.Contains(cancellationToken))
            {
                _cancellationTokens.Add(cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await action();
                await Task.Delay(interval, cancellationToken.Token);
            }
        }

        public static async Task Run(TimeSpan interval, CancellationTokenSource cancellationToken, int repetitions, Func<Task> action)
        {
            if (cancellationToken == null)
            {
                throw new ArgumentNullException(nameof(cancellationToken));
            }
            
            if (!_cancellationTokens.Contains(cancellationToken))
            {
                _cancellationTokens.Add(cancellationToken);
            }
            
            var countRepeat = 0;

            while (!cancellationToken.IsCancellationRequested && countRepeat < repetitions)
            {
                await action();
                await Task.Delay(interval, cancellationToken.Token);
                countRepeat++;
            }
        }

        public static async Task Run(TimeSpan interval, int repetitions, Func<Task> action)
        {
            Run(interval, new CancellationTokenSource(), repetitions, action);
        }

        public static void CancelAllTasks()
        {
            _cancellationTokens.ForEach(c => c.Cancel());
        }
    }
}