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
    private readonly string coreName;
    private readonly RomData rom;
    private Thread thread;
    private volatile bool running, stopRequested;
    private int disposeStarted;
    private Wrapper wrapper;

    private LibretroRuntime(string coreName, RomData rom, AudioBridge audio)
    {
        this.coreName = coreName; this.rom = rom; this.audio = audio;
        graphics = new BrGraphicsProcessor(); input = new BrInputProcessor();
    }

    internal static LibretroRuntime Start(string coreName, RomData rom, AudioBridge audio)
    {
        Current?.Dispose();
        Current = new LibretroRuntime(coreName, rom, audio);
        Current.thread = new Thread(Current.Run) { IsBackground = true, Name = "BR-Libretro" };
        Current.thread.Start();
        return Current;
    }

    private void Run()
    {
        RomContentSession content = null;
        try
        {
            content = RomContentSession.Prepare(rom);
            var settings = new WrapperSettings(Platform.Win) {
                MainDirectory = DataPaths.Root, SystemDirectory = DataPaths.Bios, TempDirectory = DataPaths.Temp, LogLevel = LogLevel.Info,
                LogProcessor = new BrLogProcessor(), GraphicsProcessor = graphics, AudioProcessor = audio,
                InputProcessor = input, LedProcessor = new NullLedProcessor(), MessageProcessor = new NullMessageProcessor()
            };
            string launchPath = content.LaunchPath;
            string directory = Path.GetDirectoryName(launchPath), name = Path.GetFileNameWithoutExtension(launchPath);
            wrapper = new Wrapper(settings, coreName, directory, new[] { name }, launchPath);
            if (!wrapper.StartContent()) { MelonLogger.Error($"Could not start core '{coreName}' with '{rom.ContentPath}'."); return; }
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
            content?.Dispose();
            MelonLogger.Msg("Libretro core thread stopped.");
        }
    }

    internal bool PumpVideo(out Texture2D texture) => graphics.Pump(out texture);
    internal void SetInputEnabled(bool enabled) => input.Enabled = enabled;
    internal void UpdateInput() => input.UpdateSnapshot();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        input.Enabled = false;
        stopRequested = true;
        Thread worker = thread;
        if (worker != null && worker.IsAlive && worker != Thread.CurrentThread && !worker.Join(10000))
        {
            MelonLogger.Error("Libretro core thread did not stop within 10 seconds. Native and Unity resources were deliberately left intact to avoid a shutdown use-after-free.");
            return;
        }

        thread = null;
        graphics.DisposeUnityResources();
        audio?.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
        MelonLogger.Msg("Libretro runtime shutdown completed cleanly.");
    }
}
