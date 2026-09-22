using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Autoroadmarking_Pro.CadHost.Commands
{
    internal sealed class QueuedCadRequest
    {
        public Document? Document { get; set; }
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
                Document = doc,
                Action = action ?? string.Empty,
                Payload = payload.ValueKind == JsonValueKind.Undefined
                    ? default
                    : payload.Clone()
            });

            ScheduleIfNeeded();
            return true;
        }

        public static bool TryDequeue(Document document, out QueuedCadRequest request)
        {
            if (document == null)
            {
                request = new QueuedCadRequest();
                return false;
            }

            // Queue dùng chung cho toàn plugin nhưng mỗi request phải chạy đúng DWG đã phát lệnh.
            // Quét tối đa số phần tử hiện có và xoay request của document khác về cuối queue.
            int attempts = Queue.Count;
            for (int i = 0; i < attempts; i++)
            {
                if (!Queue.TryDequeue(out QueuedCadRequest? item) || item == null)
                    break;

                if (SameDocument(item.Document, document))
                {
                    request = item;
                    return true;
                }

                Queue.Enqueue(item);
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

            if (!Queue.TryPeek(out QueuedCadRequest? next) || next?.Document == null)
            {
                Interlocked.Exchange(ref _scheduled, 0);
                return;
            }

            Document doc = next.Document;

            try
            {
                // Gửi command nội bộ đúng vào document đã tạo request.
                // Không dùng MdiActiveDocument ở đây: nếu đổi tab DWG giữa lúc UI gửi lệnh
                // và lúc AutoCAD nhận command, request vẫn không được phép chạy nhầm bản vẽ.
                doc.SendStringToExecute(
                    CommandNames.InternalExec + "\n",
                    true,
                    false,
                    false);
            }
            catch
            {
                // Document có thể đã đóng trước khi command được schedule. Bỏ request đầu
                // bị stale rồi tiếp tục với request còn lại thay vì khóa queue vĩnh viễn.
                Queue.TryDequeue(out _);
                Interlocked.Exchange(ref _scheduled, 0);

                if (!Queue.IsEmpty)
                    ScheduleIfNeeded();
            }
        }

        private static bool SameDocument(Document? first, Document second)
        {
            if (first == null) return false;
            if (ReferenceEquals(first, second)) return true;

            try
            {
                return ReferenceEquals(first.Database, second.Database);
            }
            catch
            {
                return false;
            }
        }
    }
}
