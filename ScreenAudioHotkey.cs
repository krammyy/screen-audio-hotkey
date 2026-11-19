using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

namespace ScreenAudioHotkey
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 0x5341;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;
        private const uint KeyR = 0x52;
        private readonly Action callback;
        private bool registered;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        public HotkeyWindow(Action callback)
        {
            this.callback = callback;
            CreateHandle(new CreateParams());
            registered = RegisterHotKey(Handle, HotkeyId, ModControl | ModShift | ModNoRepeat, KeyR);
            if (!registered)
            {
                DestroyHandle();
                throw new InvalidOperationException("Не удалось назначить Ctrl+Shift+R. Возможно, сочетание занято другой программой.");
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == HotkeyId)
                callback();
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (registered)
            {
                UnregisterHotKey(Handle, HotkeyId);
                registered = false;
            }
            DestroyHandle();
        }
    }

    internal sealed class RecorderContext : ApplicationContext
    {
        private readonly object sync = new object();
        private readonly string outputFolder;
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem toggleItem;
        private readonly HotkeyWindow hotkey;
        private WasapiLoopbackCapture capture;
        private WaveFileWriter writer;
        private string currentFile;
        private bool exiting;

        public RecorderContext()
        {
            outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenAudioHotkey");
            Directory.CreateDirectory(outputFolder);

            toggleItem = new ToolStripMenuItem("Начать запись (Ctrl+Shift+R)", null, delegate { ToggleRecording(); });
            var openItem = new ToolStripMenuItem("Открыть папку с записями", null, delegate
            {
                Process.Start("explorer.exe", "\"" + outputFolder + "\"");
            });
            var exitItem = new ToolStripMenuItem("Выход", null, delegate { ExitRecorder(); });
            var menu = new ContextMenuStrip();
            menu.Items.Add(toggleItem);
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Запись звука с экрана — готово",
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ToggleRecording(); };
            hotkey = new HotkeyWindow(ToggleRecording);

            trayIcon.BalloonTipTitle = "Запись звука с экрана";
            trayIcon.BalloonTipText = "Готово. Ctrl+Shift+R — начать или завершить запись.";
            trayIcon.ShowBalloonTip(3000);
        }

        private void ToggleRecording()
        {
            lock (sync)
            {
                if (capture == null)
                    StartRecording();
                else
                    StopRecording();
            }
        }

        private void StartRecording()
        {
            try
            {
                currentFile = Path.Combine(outputFolder, "ScreenAudio_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".wav");
                capture = new WasapiLoopbackCapture();
                writer = new WaveFileWriter(currentFile, capture.WaveFormat);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                toggleItem.Text = "Завершить запись (Ctrl+Shift+R)";
                trayIcon.Icon = SystemIcons.Error;
                trayIcon.Text = "Запись звука с экрана — ИДЁТ ЗАПИСЬ";
                trayIcon.BalloonTipTitle = "Запись началась";
                trayIcon.BalloonTipText = "Повторно нажмите Ctrl+Shift+R для завершения.";
                trayIcon.ShowBalloonTip(1500);
            }
            catch (Exception error)
            {
                CleanupCapture();
                MessageBox.Show("Не удалось начать запись:\n" + error.Message,
                    "Запись звука с экрана", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopRecording()
        {
            try
            {
                capture.StopRecording();
            }
            catch (Exception error)
            {
                CleanupCapture();
                MessageBox.Show("Не удалось корректно завершить запись:\n" + error.Message,
                    "Запись звука с экрана", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDataAvailable(object sender, WaveInEventArgs args)
        {
            lock (sync)
            {
                if (writer != null && args.BytesRecorded > 0)
                    writer.Write(args.Buffer, 0, args.BytesRecorded);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs args)
        {
            string savedFile;
            lock (sync)
            {
                savedFile = currentFile;
                CleanupCapture();
            }

            if (!exiting)
            {
                toggleItem.Text = "Начать запись (Ctrl+Shift+R)";
                trayIcon.Icon = SystemIcons.Application;
                trayIcon.Text = "Запись звука с экрана — готово";
                trayIcon.BalloonTipTitle = args.Exception == null ? "Запись сохранена" : "Запись остановлена с ошибкой";
                trayIcon.BalloonTipText = args.Exception == null
                    ? Path.GetFileName(savedFile)
                    : args.Exception.Message;
                trayIcon.ShowBalloonTip(2500);
            }
        }

        private void CleanupCapture()
        {
            if (capture != null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
            }
            if (writer != null)
            {
                writer.Dispose();
                writer = null;
            }
            if (capture != null)
            {
                capture.Dispose();
                capture = null;
            }
            currentFile = null;
        }

        private void ExitRecorder()
        {
            exiting = true;
            WasapiLoopbackCapture active;
            lock (sync) { active = capture; }
            if (active != null)
            {
                try { active.StopRecording(); }
                catch { lock (sync) { CleanupCapture(); } }
                var until = DateTime.UtcNow.AddSeconds(2);
                while (capture != null && DateTime.UtcNow < until)
                {
                    Application.DoEvents();
                    Thread.Sleep(25);
                }
            }
            lock (sync) { CleanupCapture(); }
            hotkey.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
                ExitRecorder();
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new RecorderContext());
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Запись звука с экрана",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
