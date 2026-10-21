using System;
using System.Threading.Tasks;
using Uviewer.Models;

namespace Uviewer.Services
{
    // Called on the UI thread. Keep rendering serial while retaining the latest
    // committed selection, so the controls can stay editable during chapter loads.
    internal sealed class EpubNavigationQueue
    {
        private readonly Func<int, int, Task<EpubNavigationPosition>> _navigateAsync;
        private readonly Action<EpubNavigationPosition, long> _applyPosition;
        private readonly Action<bool> _setBusy;
        private readonly Action<Exception> _onError;
        private Request? _pending;
        private Request? _active;
        private bool _isRunning;

        private readonly record struct Request(int Chapter, int Line, long Revision);

        public Task Completion { get; private set; } = Task.CompletedTask;

        public EpubNavigationQueue(
            Func<int, int, Task<EpubNavigationPosition>> navigateAsync,
            Action<EpubNavigationPosition, long> applyPosition,
            Action<bool> setBusy,
            Action<Exception> onError)
        {
            _navigateAsync = navigateAsync;
            _applyPosition = applyPosition;
            _setBusy = setBusy;
            _onError = onError;
        }

        public void Enqueue(int chapter, int line, long revision)
        {
            var request = new Request(chapter, line, revision);
            if (_pending == request || (_active == request && _pending == null)) return;
            _pending = request;
            if (_isRunning) return;

            _isRunning = true;
            _setBusy(true);
            Completion = ProcessAsync();
        }

        private async Task ProcessAsync()
        {
            try
            {
                while (_pending is Request request)
                {
                    _pending = null;
                    _active = request;
                    try
                    {
                        var position = await _navigateAsync(request.Chapter, request.Line);
                        if (_pending == null) _applyPosition(position, request.Revision);
                    }
                    catch (OperationCanceledException ex)
                    {
                        _pending = null;
                        _onError(ex);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (_pending == null) _onError(ex);
                    }
                }
            }
            finally
            {
                _active = null;
                _isRunning = false;
                _setBusy(false);
            }
        }
    }
}
