using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SharpNes.Core;
using SDL2;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run --project src/SharpNes.App/SharpNes.App.csproj -- <path-to-rom.nes>");
            return 1;
        }

        string romPath = args[0];
        if (!File.Exists(romPath))
        {
            Console.WriteLine($"ROM not found: {romPath}");
            return 1;
        }

        byte[] romBytes = File.ReadAllBytes(romPath);
        Cartridge cart = Cartridge.LoadINES(romBytes);

        Console.WriteLine($"Loaded: {Path.GetFileName(romPath)} (Mapper {cart.MapperNumber})");

        // Initialize SDL
        if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMECONTROLLER) < 0)
        {
            Console.WriteLine($"SDL init failed: {SDL.SDL_GetError()}");
            return 1;
        }

        // Open first available joystick (using raw joystick API for correct button mapping)
        IntPtr joystick = IntPtr.Zero;
        if (SDL.SDL_NumJoysticks() > 0)
        {
            joystick = SDL.SDL_JoystickOpen(0);
            if (joystick != IntPtr.Zero)
            {
                string? name = SDL.SDL_JoystickName(joystick);
                Console.WriteLine($"Controller: {name ?? "Unknown"}");
                Console.WriteLine($"Buttons: {SDL.SDL_JoystickNumButtons(joystick)}, Axes: {SDL.SDL_JoystickNumAxes(joystick)}");
            }
        }
        if (joystick == IntPtr.Zero)
        {
            Console.WriteLine("No controller found - using keyboard only");
        }

        // Create window (3x scale)
        const int scale = 3;
        IntPtr window = SDL.SDL_CreateWindow(
            $"SharpNes - {Path.GetFileName(romPath)}",
            SDL.SDL_WINDOWPOS_CENTERED,
            SDL.SDL_WINDOWPOS_CENTERED,
            256 * scale,
            240 * scale,
            SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN
        );

        if (window == IntPtr.Zero)
        {
            Console.WriteLine($"Window creation failed: {SDL.SDL_GetError()}");
            SDL.SDL_Quit();
            return 1;
        }

        // Create renderer
        IntPtr renderer = SDL.SDL_CreateRenderer(window, -1,
            SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED |
            SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);

        if (renderer == IntPtr.Zero)
        {
            Console.WriteLine($"Renderer creation failed: {SDL.SDL_GetError()}");
            SDL.SDL_DestroyWindow(window);
            SDL.SDL_Quit();
            return 1;
        }

        // Create texture for framebuffer
        IntPtr texture = SDL.SDL_CreateTexture(
            renderer,
            SDL.SDL_PIXELFORMAT_ARGB8888,
            (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
            256,
            240
        );

        if (texture == IntPtr.Zero)
        {
            Console.WriteLine($"Texture creation failed: {SDL.SDL_GetError()}");
            SDL.SDL_DestroyRenderer(renderer);
            SDL.SDL_DestroyWindow(window);
            SDL.SDL_Quit();
            return 1;
        }

        // Set up audio
        uint audioDevice = 0;
        try
        {
            int numAudioDevices = SDL.SDL_GetNumAudioDevices(0);
            Console.WriteLine($"Audio devices found: {numAudioDevices}");

            for (int i = 0; i < numAudioDevices; i++)
            {
                Console.WriteLine($"  [{i}] {SDL.SDL_GetAudioDeviceName(i, 0)}");
            }

            SDL.SDL_AudioSpec wantedSpec = new SDL.SDL_AudioSpec
            {
                freq = 44100,
                format = SDL.AUDIO_F32,
                channels = 1,
                samples = 1024,
                callback = null
            };

            // Try to find HDMI/NVidia device first, then fall back to others
            string? preferredDevice = null;
            string? fallbackDevice = null;

            for (int i = 0; i < numAudioDevices; i++)
            {
                string? deviceName = SDL.SDL_GetAudioDeviceName(i, 0);
                if (deviceName == null) continue;

                // Skip microphone devices
                if (deviceName.Contains("microphone", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("mic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Prefer HDMI devices
                if (deviceName.Contains("HDMI", StringComparison.OrdinalIgnoreCase))
                {
                    preferredDevice = deviceName;
                }
                else if (fallbackDevice == null)
                {
                    fallbackDevice = deviceName;
                }
            }

            string? deviceToUse = preferredDevice ?? fallbackDevice;
            if (deviceToUse != null)
            {
                audioDevice = SDL.SDL_OpenAudioDevice(
                    deviceToUse, 0, ref wantedSpec, out SDL.SDL_AudioSpec obtained, 0);
                if (audioDevice != 0)
                {
                    Console.WriteLine($"Audio opened: {deviceToUse}");
                    Console.WriteLine($"  {obtained.freq}Hz, {obtained.channels}ch, buffer {obtained.samples}");
                    SDL.SDL_PauseAudioDevice(audioDevice, 0);
                }
            }

            if (audioDevice == 0)
            {
                Console.WriteLine($"Failed to open any audio device: {SDL.SDL_GetError()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Audio init exception: {ex.Message}");
            audioDevice = 0;
        }

        // Set up emulator
        var bus = new Bus();
        bus.InsertCartridge(cart);

        var cpu = new Cpu6502(bus);
        cpu.Reset();

        // Audio buffer
        float[] audioBuffer = new float[4096];

        // PPU sync callback
        bus.SyncPpuCallback = () =>
        {
            long currentExpectedPpu = (cpu.TotalCycles + 4) * 3;
            long ppuBehind = currentExpectedPpu - bus.Ppu.TotalPpuCycles;
            if (ppuBehind > 0)
            {
                bus.Ppu.TickMany(ppuBehind);
            }
        };

        bool running = true;

        // Frame timing for 60 FPS (NTSC)
        const double targetFrameTime = 1000.0 / 60.0988; // ~16.64ms per frame
        var frameTimer = Stopwatch.StartNew();
        double nextFrameTime = 0;

        while (running)
        {
            // Handle SDL events
            while (SDL.SDL_PollEvent(out SDL.SDL_Event e) != 0)
            {
                switch (e.type)
                {
                    case SDL.SDL_EventType.SDL_QUIT:
                        running = false;
                        break;

                    case SDL.SDL_EventType.SDL_KEYDOWN:
                    case SDL.SDL_EventType.SDL_KEYUP:
                        {
                            bool pressed = e.type == SDL.SDL_EventType.SDL_KEYDOWN;
                            switch (e.key.keysym.sym)
                            {
                                case SDL.SDL_Keycode.SDLK_ESCAPE:
                                    if (pressed) running = false;
                                    break;
                                // Arrow keys for D-pad
                                case SDL.SDL_Keycode.SDLK_UP:
                                    bus.Controller1.SetButton(NesController.ButtonUp, pressed);
                                    break;
                                case SDL.SDL_Keycode.SDLK_DOWN:
                                    bus.Controller1.SetButton(NesController.ButtonDown, pressed);
                                    break;
                                case SDL.SDL_Keycode.SDLK_LEFT:
                                    bus.Controller1.SetButton(NesController.ButtonLeft, pressed);
                                    break;
                                case SDL.SDL_Keycode.SDLK_RIGHT:
                                    bus.Controller1.SetButton(NesController.ButtonRight, pressed);
                                    break;
                                // Z = A, X = B
                                case SDL.SDL_Keycode.SDLK_x:
                                    bus.Controller1.SetButton(NesController.ButtonA, pressed);
                                    break;
                                case SDL.SDL_Keycode.SDLK_z:
                                    bus.Controller1.SetButton(NesController.ButtonB, pressed);
                                    break;
                                // Enter = Start, RShift = Select
                                case SDL.SDL_Keycode.SDLK_RETURN:
                                    bus.Controller1.SetButton(NesController.ButtonStart, pressed);
                                    break;
                                case SDL.SDL_Keycode.SDLK_RSHIFT:
                                    bus.Controller1.SetButton(NesController.ButtonSelect, pressed);
                                    break;
                            }
                        }
                        break;

                    case SDL.SDL_EventType.SDL_JOYBUTTONDOWN:
                    case SDL.SDL_EventType.SDL_JOYBUTTONUP:
                        {
                            bool pressed = e.type == SDL.SDL_EventType.SDL_JOYBUTTONDOWN;
                            byte btn = e.jbutton.button;

                            // Xbox 360 Bluetooth raw button mapping
                            switch (btn)
                            {
                                case 0: // A
                                    bus.Controller1.SetButton(NesController.ButtonA, pressed);
                                    break;
                                case 1: // B
                                case 3: // X (also maps to B)
                                    bus.Controller1.SetButton(NesController.ButtonB, pressed);
                                    break;
                                case 10: // Back/View → Select
                                    bus.Controller1.SetButton(NesController.ButtonSelect, pressed);
                                    break;
                                case 11: // Menu/Start → Start
                                    bus.Controller1.SetButton(NesController.ButtonStart, pressed);
                                    break;
                            }
                        }
                        break;

                    case SDL.SDL_EventType.SDL_JOYHATMOTION:
                        {
                            // D-pad is typically a hat on Xbox controllers
                            byte hat = e.jhat.hatValue;
                            bus.Controller1.SetButton(NesController.ButtonUp, (hat & SDL.SDL_HAT_UP) != 0);
                            bus.Controller1.SetButton(NesController.ButtonDown, (hat & SDL.SDL_HAT_DOWN) != 0);
                            bus.Controller1.SetButton(NesController.ButtonLeft, (hat & SDL.SDL_HAT_LEFT) != 0);
                            bus.Controller1.SetButton(NesController.ButtonRight, (hat & SDL.SDL_HAT_RIGHT) != 0);
                        }
                        break;

                }
            }

            if (!running) break;

            // Run CPU until frame is ready
            long lastCpuCycles = cpu.TotalCycles;
            try
            {
                while (!bus.Ppu.FrameReady)
                {
                    cpu.Step();

                    // Calculate CPU cycles elapsed this step
                    long cpuCyclesElapsed = cpu.TotalCycles - lastCpuCycles;
                    lastCpuCycles = cpu.TotalCycles;

                    // Handle OAM DMA
                    if (bus.OamDmaRequested)
                    {
                        int dmaCycles = bus.ExecuteOamDma();
                        // Account for DMA cycles in PPU timing
                        bus.Ppu.TickMany(dmaCycles * 3);
                        cpuCyclesElapsed += dmaCycles;
                    }

                    // Catch up PPU
                    long expectedPpu = cpu.TotalCycles * 3;
                    long ppuBehind = expectedPpu - bus.Ppu.TotalPpuCycles;
                    if (ppuBehind > 0)
                    {
                        bus.Ppu.TickMany(ppuBehind);
                    }

                    // Tick APU with actual CPU cycles
                    bus.Apu.Tick((int)cpuCyclesElapsed);

                    // Check for NMI
                    if (bus.Ppu.TryConsumeNmi())
                    {
                        cpu.Nmi();
                    }

                    // Check for mapper IRQ (MMC3)
                    if (bus.MapperIrqPending)
                    {
                        bus.AcknowledgeMapperIrq();
                        cpu.Irq();
                    }
                }

                bus.Ppu.FrameReady = false;

                // Update texture with framebuffer
                unsafe
                {
                    fixed (byte* pixels = bus.Ppu.Framebuffer)
                    {
                        SDL.SDL_UpdateTexture(texture, IntPtr.Zero, (IntPtr)pixels, 256 * 4);
                    }
                }

                // Render
                SDL.SDL_RenderClear(renderer);
                SDL.SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero);
                SDL.SDL_RenderPresent(renderer);

                // Queue audio samples (limit buffer to reduce latency)
                if (audioDevice != 0)
                {
                    uint queued = SDL.SDL_GetQueuedAudioSize(audioDevice);
                    uint maxQueued = 44100 * sizeof(float) / 15; // ~66ms max latency

                    if (queued < maxQueued)
                    {
                        int samples = bus.Apu.GetSamples(audioBuffer, audioBuffer.Length);
                        if (samples > 0)
                        {
                            unsafe
                            {
                                fixed (float* ptr = audioBuffer)
                                {
                                    SDL.SDL_QueueAudio(audioDevice, (IntPtr)ptr, (uint)(samples * sizeof(float)));
                                }
                            }
                        }
                    }
                }

                // Frame timing - wait until next frame time
                nextFrameTime += targetFrameTime;
                double currentTime = frameTimer.Elapsed.TotalMilliseconds;
                double sleepTime = nextFrameTime - currentTime;
                if (sleepTime > 0)
                {
                    Thread.Sleep((int)sleepTime);
                }
                else if (sleepTime < -100)
                {
                    // If we're way behind, reset timing
                    nextFrameTime = currentTime;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Emulation error: {ex.Message}");
                Console.WriteLine($"PC=${cpu.PC:X4} A=${cpu.A:X2} X=${cpu.X:X2} Y=${cpu.Y:X2}");
                running = false;
            }
        }

        // Cleanup
        if (joystick != IntPtr.Zero)
            SDL.SDL_JoystickClose(joystick);
        if (audioDevice != 0)
            SDL.SDL_CloseAudioDevice(audioDevice);
        SDL.SDL_DestroyTexture(texture);
        SDL.SDL_DestroyRenderer(renderer);
        SDL.SDL_DestroyWindow(window);
        SDL.SDL_Quit();

        return 0;
    }
}
