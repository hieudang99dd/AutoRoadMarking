using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Autoroadmarking_Pro.CadHost.Commands
{
    internal sealed class QueuedCadRequest
    {
        public string Action { get; set; } = string.Empty;
        public JsonElement Payload { get; set; }
    }

    /// <summary>
    /// Hàng đợi bridge WebView2 -> AutoCAD command context.
    /// WebView2 callback không được thao tác Database trực tiếp; mọi CAD action được
    /// chuyển sang ARM_INTERNAL_EXEC để chạy trong command context của AutoCAD.
    /// </summary>
    internal static class CadCommandQueue
    {
        private static readonly ConcurrentQueue<QueuedCadRequest> Queue =
            new ConcurrentQueue<QueuedCadRequest>();

        private static int _scheduled;

        public static bool Enqueue(string action, JsonElement payload)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return false;

            Queue.Enqueue(new QueuedCadRequest
            {
                Action = action ?? string.Empty,
                Payload = payload.ValueKind == JsonValueKind.Undefined
                    ? default
                    : payload.Clone()
            });

            ScheduleIfNeeded();
            return true;
        }

        public static bool TryDequeue(out QueuedCadRequest request)
        {
            if (Queue.TryDequeue(out QueuedCadRequest? item) && item != null)
            {
                request = item;
                return true;
            }

            request = new QueuedCadRequest();
            return false;
        }

        public static void MarkCommandFinished()
        {
            Interlocked.Exchange(ref _scheduled, 0);

            if (!Queue.IsEmpty)
                ScheduleIfNeeded();
        }

        private static void ScheduleIfNeeded()
        {
            if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
                return;

            var doc = AcApp.DocumentManager.MdiActiveDocument;

            if (doc == null)
            {
                Interlocked.Exchange(ref _scheduled, 0);
                return;
            }

            // Gửi command nội bộ vào AutoCAD để có document/command context hợp lệ.
            doc.SendStringToExecute(
                CommandNames.InternalExec + "\n",
                true,
                false,
                false);
        }
    }
}
