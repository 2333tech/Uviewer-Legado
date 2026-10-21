using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Uviewer.Services.Legado.BookSources;

/// <summary>
/// 搜索窗口的<b>优先级工作队列</b>（目录/可访问性探测等后台任务共用）。
/// <para><b>为什么不用 SemaphoreSlim</b>：搜索可能一次返回几百条结果，后台探测必须限流
/// （否则瞬间打出上百个请求把站点打崩）；但用户<b>双击某条结果</b>或点开它的目录时，
/// 那一条必须立刻插队——SemaphoreSlim 只会 FIFO，插不了队。本队列用
/// 「(优先级, 入队序) 排序的待办表 + 固定 worker」实现：低优先级任务排队等 worker，
/// <see cref="IPriorityWorkHandle.Boost"/> 把指定任务提到队首（已在执行中的无法抢占——
/// 网络请求一旦发出只能等它回来，这是物理限制；插队消除的是<b>排队等待</b>）。</para>
/// <para>数值约定：数字越小越优先（0 = 交互级，1 = 后台级）。</para>
/// </summary>
internal sealed class PriorityWorkQueue : IDisposable
{
    private sealed class WorkItem : IComparable<WorkItem>
    {
        public WorkItem(int priority, long seq, Func<CancellationToken, Task> work)
        {
            Priority = priority;
            Seq = seq;
            Work = work;
        }

        public int Priority { get; set; }

        public long Seq { get; }

        /// <summary>已开始执行（插队对执行中的任务无效）。</summary>
        public volatile bool Started;

        public Func<CancellationToken, Task> Work;

        public int CompareTo(WorkItem? other) =>
            other is null ? 1
            : Priority != other.Priority ? Priority.CompareTo(other.Priority)
            : Seq.CompareTo(other.Seq);
    }

    /// <summary>排队句柄：拿到它的一方（卡片 UI）可以在用户交互时把任务提到队首。</summary>
    public interface IPriorityWorkHandle
    {
        /// <summary>把该任务提升为交互级（0）。已在执行中的调用是空操作。</summary>
        void Boost();
    }

    private readonly object _gate = new();
    private readonly List<WorkItem> _pending = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<Task> _workers = new();
    private readonly int _maxConcurrency;
    private long _seq;
    private int _running;

    public PriorityWorkQueue(int maxConcurrency)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        _maxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// 入队一个后台任务。worker 数量固定（<see cref="PriorityWorkQueue(int)"/>），
    /// 超出并发能力的任务在待办表里按 (优先级, 入队序) 排队。
    /// </summary>
    /// <param name="priority">数字越小越优先（0 = 交互级，1 = 后台级）。</param>
    /// <param name="work">任务体；收到的 token 在队列 Dispose 时取消（卡片级的取消由调用方自带）。</param>
    public IPriorityWorkHandle Enqueue(int priority, Func<CancellationToken, Task> work)
    {
        var item = new WorkItem(priority, Interlocked.Increment(ref _seq), work);
        lock (_gate)
        {
            _pending.Add(item);
            Monitor.Pulse(_gate);
        }

        EnsureWorkers();
        return new Handle(item);
    }

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            while (_workers.Count < _maxConcurrency)
            {
                _workers.Add(Task.Run(WorkerLoopAsync, CancellationToken.None));
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var token = _disposeCts.Token;
        while (!token.IsCancellationRequested)
        {
            WorkItem? item;
            lock (_gate)
            {
                while (_pending.Count == 0 && !token.IsCancellationRequested)
                {
                    Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (_pending.Count == 0)
                {
                    continue;
                }

                // 待办表按 (优先级, 入队序) 排序后取队首（Insert 排序 or Min？列表小、插多取少，
                // 直接排序一次 O(n log n) 不如线性 Min——这里用线性 Min（n = 待办数，通常 < 几百）
                item = _pending[0];
                for (var i = 1; i < _pending.Count; i++)
                {
                    if (_pending[i].CompareTo(item) < 0)
                    {
                        item = _pending[i];
                    }
                }

                _pending.Remove(item);
                item.Started = true;
                Interlocked.Increment(ref _running);
            }

            try
            {
                await item.Work(token).ConfigureAwait(false);
            }
            catch
            {
                // 工作体自带异常语义（卡片探测失败 → 显示错误标记），队列不吞不抛
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        lock (_gate)
        {
            _pending.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    private sealed class Handle(WorkItem item) : IPriorityWorkHandle
    {
        public void Boost()
        {
            if (!item.Started)
            {
                item.Priority = 0;
            }
        }
    }
}
