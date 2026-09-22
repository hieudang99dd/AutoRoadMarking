using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using Autoroadmarking_Pro.CadHost.Cad;
using Autoroadmarking_Pro.CadHost.UI;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Autoroadmarking_Pro.CadHost.Commands
{
    public sealed class RoadMarkingCommands : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Document? doc = AcApp.DocumentManager.MdiActiveDocument;
                Editor? ed = doc?.Editor;

                ed?.WriteMessage(
                    "\n[AutoRoadMarking] CadHost NETLOAD thành công." +
                    "\n[AutoRoadMarking] Lệnh: HDV_VACHSON | ARM_TEST\n");
            }
            catch
            {
                // Không để thông báo khởi động làm hỏng quá trình NETLOAD.
            }
        }

        public void Terminate()
        {
        }

        [CommandMethod(CommandNames.Test, CommandFlags.Modal)]
        public void TestCommandRegistration()
        {
            Document? doc = AcApp.DocumentManager.MdiActiveDocument;

            doc?.Editor.WriteMessage(
                "\n[AutoRoadMarking] ARM_TEST OK - command registration hoạt động.\n");
        }

        [CommandMethod(CommandNames.Main, CommandFlags.Modal)]
        public void ShowMainWindow()
        {
            try
            {
                MainWindowManager.Show();
            }
            catch (System.Exception ex)
            {
                Document? doc = AcApp.DocumentManager.MdiActiveDocument;

                doc?.Editor.WriteMessage(
                    "\n[AutoRoadMarking] HDV_VACHSON lỗi: " +
                    ex.GetType().FullName +
                    " - " +
                    ex.Message +
                    "\n");

                throw;
            }
        }

        [CommandMethod(
            CommandNames.InternalExec,
            CommandFlags.Modal | CommandFlags.NoHistory)]
        public void ExecuteQueuedCadActions()
        {
            var executor = new CadActionExecutor();

            try
            {
                while (CadCommandQueue.TryDequeue(out QueuedCadRequest request))
                {
                    bool interactive =
                        CadActionExecutor.IsInteractive(request.Action, request.Payload);

                    if (interactive)
                        MainWindowManager.HideForCadInput();

                    WebResponse response;

                    try
                    {
                        response = executor.Execute(
                            request.Action,
                            request.Payload);
                    }
                    catch (System.Exception ex)
                    {
                        response = WebResponse.Fail(
                            request.Action,
                            ex.Message);
                    }
                    finally
                    {
                        if (interactive)
                            MainWindowManager.RestoreAfterCadInput();
                    }

                    MainWindowManager.PostResponse(response);
                }
            }
            finally
            {
                CadCommandQueue.MarkCommandFinished();
            }
        }
    }
}
