using MelonLoader;
using SK.Libretro;
using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace BR_Libretro;

public sealed class LibretroRuntime : IDisposable
{
    public static LibretroRuntime Current { get; private set; }
    public static bool IsRunning => Current?.running == true;
    public Texture OutputTexture => graphics?.Texture;
    internal bool HasExited => thread != null && !thread.IsAlive;

    private readonly BrGraphicsProcessor graphics;
    private readonly BrInputProcessor input;
    private readonly AudioBridge audio;
    private readonly string coreName, romPath;
    private Thread thread;
    private volatile bool running, stopRequested;
    private Wrapper wrapper;

    private LibretroRuntime(string coreName, string romPath, AudioBridge audio)
    {
        this.coreName = coreName; this.romPath = romPath; this.audio = audio;
        graphics = new BrGraphicsProcessor(); input = new BrInputProcessor();
    }

    internal static LibretroRuntime Start(string coreName, string romPath, AudioBridge audio)
    {
        Current?.Dispose();
        Current = new LibretroRuntime(coreName, romPath, audio);
        Current.thread = new Thread(Current.Run) { IsBackground = true, Name = "BR-Libretro" };
        Current.thread.Start();
        return Current;
    }

    private void Run()
    {
        try
        {
            var settings = new WrapperSettings(Platform.Win) {
                MainDirectory = DataPaths.Root, SystemDirectory = DataPaths.Bios, TempDirectory = DataPaths.Temp, LogLevel = LogLevel.Info,
                LogProcessor = new BrLogProcessor(), GraphicsProcessor = graphics, AudioProcessor = audio,
                InputProcessor = input, LedProcessor = new NullLedProcessor(), MessageProcessor = new NullMessageProcessor()
            };
            string directory = Path.GetDirectoryName(romPath), name = Path.GetFileNameWithoutExtension(romPath);
            wrapper = new Wrapper(settings, coreName, directory, new[] { name });
            if (!wrapper.StartContent()) { MelonLogger.Error($"Could not start core '{coreName}' with '{romPath}'."); return; }
            MelonLogger.Msg($"Libretro content loaded: {wrapper.Core.SystemInfo.LibraryName} {wrapper.Core.SystemInfo.LibraryVersion}, {wrapper.Game.SystemAVInfo.BaseWidth}x{wrapper.Game.SystemAVInfo.BaseHeight} @ {wrapper.Game.SystemAVInfo.Fps:0.##} fps, native audio {wrapper.Game.SystemAVInfo.SampleRate} Hz.");
            wrapper.InitGraphics(); wrapper.InitAudio(); wrapper.InputHandler.Enabled = true;
            running = true;
            double frameMs = 1000.0 / Math.Max(1.0, wrapper.Game.SystemAVInfo.Fps);
            var clock = System.Diagnostics.Stopwatch.StartNew(); double next = 0;
            while (!stopRequested)
            {
                if (clock.Elapsed.TotalMilliseconds >= next) { wrapper.RunFrame(); next += frameMs; }
                else Thread.Sleep(1);
            }
        }
        catch (Exception ex) { MelonLogger.Error("Libretro runtime failed: " + ex); }
        finally
        {
            try { wrapper?.StopContent(); } catch (Exception ex) { MelonLogger.Warning("Libretro shutdown: " + ex.Message); }
            wrapper = null; running = false;
        }
    }

    internal bool PumpVideo(out Texture2D texture) => graphics.Pump(out texture);
    internal void SetInputEnabled(bool enabled) => input.Enabled = enabled;
    internal void UpdateInput() => input.UpdateSnapshot();
    public void Dispose()
    {
        stopRequested = true;
        if (thread != null && thread.IsAlive) thread.Join(2000);
        graphics.Dispose(); audio?.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
