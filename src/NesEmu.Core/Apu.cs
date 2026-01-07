using System;

namespace NesEmu.Core;

public sealed class Apu
{
    // Audio output buffer
    private readonly float[] _sampleBuffer = new float[4096];
    private int _sampleIndex;

    // CPU cycle tracking for sample generation
    private double _cycleAccumulator;
    private const double CpuFrequency = 1789773.0;  // NTSC CPU frequency
    private const double SampleRate = 44100.0;
    private const double CyclesPerSample = CpuFrequency / SampleRate;

    // Pulse channel 1
    private byte _pulse1Duty;
    private byte _pulse1Volume;
    private bool _pulse1Constant;
    private bool _pulse1Halt;      // Length counter halt / envelope loop (bit 5)
    private bool _pulse1Enabled;
    private ushort _pulse1Timer;
    private ushort _pulse1TimerValue;
    private int _pulse1Sequence;
    private byte _pulse1LengthCounter;
    // Sweep
    private bool _pulse1SweepEnabled;
    private byte _pulse1SweepPeriod;
    private bool _pulse1SweepNegate;
    private byte _pulse1SweepShift;
    private byte _pulse1SweepDivider;
    private bool _pulse1SweepReload;
    // Envelope
    private byte _pulse1EnvelopeVolume;
    private byte _pulse1EnvelopeDivider;
    private bool _pulse1EnvelopeStart;

    // Pulse channel 2
    private byte _pulse2Duty;
    private byte _pulse2Volume;
    private bool _pulse2Constant;
    private bool _pulse2Halt;      // Length counter halt / envelope loop (bit 5)
    private bool _pulse2Enabled;
    private ushort _pulse2Timer;
    private ushort _pulse2TimerValue;
    private int _pulse2Sequence;
    private byte _pulse2LengthCounter;
    // Sweep
    private bool _pulse2SweepEnabled;
    private byte _pulse2SweepPeriod;
    private bool _pulse2SweepNegate;
    private byte _pulse2SweepShift;
    private byte _pulse2SweepDivider;
    private bool _pulse2SweepReload;
    // Envelope
    private byte _pulse2EnvelopeVolume;
    private byte _pulse2EnvelopeDivider;
    private bool _pulse2EnvelopeStart;

    // Triangle channel
    private bool _triangleEnabled;
    private ushort _triangleTimer;
    private ushort _triangleTimerValue;
    private int _triangleSequence;
    private byte _triangleLengthCounter;

    // Noise channel
    private bool _noiseEnabled;
    private ushort _noiseTimer;
    private ushort _noiseTimerValue;
    private ushort _noiseShift = 1;
    private bool _noiseMode;
    private byte _noiseVolume;
    private bool _noiseConstant;
    private bool _noiseHalt;       // Length counter halt / envelope loop (bit 5)
    private byte _noiseLengthCounter;
    // Envelope
    private byte _noiseEnvelopeVolume;
    private byte _noiseEnvelopeDivider;
    private bool _noiseEnvelopeStart;

    // DMC channel
    private bool _dmcEnabled;
    private bool _dmcIrqEnabled;
    private bool _dmcLoop;
    private ushort _dmcTimer;
    private ushort _dmcTimerValue;
    private byte _dmcOutput = 64;          // 7-bit output level (0-127)
    private ushort _dmcSampleAddress;      // Current address
    private ushort _dmcSampleAddressStart; // Starting address ($C000 + A*64)
    private int _dmcSampleLength;          // Bytes remaining
    private int _dmcSampleLengthStart;     // Starting length (L*16 + 1)
    private byte _dmcSampleBuffer;         // Current sample byte
    private int _dmcBitsRemaining;         // Bits left in current byte
    private bool _dmcSilence;              // True when sample buffer is empty
    private Func<ushort, byte>? _dmcReadMemory;  // Callback to read CPU memory

    // Status register
    private byte _status;

    // Frame counter
    private int _frameCounter;
    private int _frameStep;
    private const int FrameCounterPeriod = 5592; // Quarter frame period (adjusted)

    // APU clock divider (pulse/noise tick every 2 CPU cycles)
    private bool _apuClockToggle;

    // Duty cycle sequences (8 steps each)
    private static readonly byte[][] DutyTable = {
        new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 },  // 12.5%
        new byte[] { 0, 1, 1, 0, 0, 0, 0, 0 },  // 25%
        new byte[] { 0, 1, 1, 1, 1, 0, 0, 0 },  // 50%
        new byte[] { 1, 0, 0, 1, 1, 1, 1, 1 },  // 75% (inverted 25%)
    };

    // Triangle sequence (32 steps)
    private static readonly byte[] TriangleTable = {
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
    };

    // Noise period lookup
    private static readonly ushort[] NoisePeriodTable = {
        4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068
    };

    // Length counter lookup
    private static readonly byte[] LengthTable = {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
    };

    // DMC rate table (NTSC, in CPU cycles)
    private static readonly ushort[] DmcRateTable = {
        428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54
    };

    // Set memory read callback for DMC
    public void SetMemoryReader(Func<ushort, byte> readMemory)
    {
        _dmcReadMemory = readMemory;
    }

    public void CpuWrite(ushort addr, byte value)
    {
        switch (addr)
        {
            // Pulse 1
            case 0x4000:
                _pulse1Duty = (byte)((value >> 6) & 0x03);
                _pulse1Halt = (value & 0x20) != 0;      // Bit 5: length counter halt / envelope loop
                _pulse1Constant = (value & 0x10) != 0;  // Bit 4: constant volume
                _pulse1Volume = (byte)(value & 0x0F);
                break;
            case 0x4001:
                _pulse1SweepEnabled = (value & 0x80) != 0;
                _pulse1SweepPeriod = (byte)((value >> 4) & 0x07);
                _pulse1SweepNegate = (value & 0x08) != 0;
                _pulse1SweepShift = (byte)(value & 0x07);
                _pulse1SweepReload = true;
                break;
            case 0x4002:
                _pulse1TimerValue = (ushort)((_pulse1TimerValue & 0x700) | value);
                break;
            case 0x4003:
                _pulse1TimerValue = (ushort)((_pulse1TimerValue & 0x0FF) | ((value & 0x07) << 8));
                if (_pulse1Enabled)
                    _pulse1LengthCounter = LengthTable[(value >> 3) & 0x1F];
                _pulse1Sequence = 0;
                _pulse1EnvelopeStart = true;
                break;

            // Pulse 2
            case 0x4004:
                _pulse2Duty = (byte)((value >> 6) & 0x03);
                _pulse2Halt = (value & 0x20) != 0;      // Bit 5: length counter halt / envelope loop
                _pulse2Constant = (value & 0x10) != 0;  // Bit 4: constant volume
                _pulse2Volume = (byte)(value & 0x0F);
                break;
            case 0x4005:
                _pulse2SweepEnabled = (value & 0x80) != 0;
                _pulse2SweepPeriod = (byte)((value >> 4) & 0x07);
                _pulse2SweepNegate = (value & 0x08) != 0;
                _pulse2SweepShift = (byte)(value & 0x07);
                _pulse2SweepReload = true;
                break;
            case 0x4006:
                _pulse2TimerValue = (ushort)((_pulse2TimerValue & 0x700) | value);
                break;
            case 0x4007:
                _pulse2TimerValue = (ushort)((_pulse2TimerValue & 0x0FF) | ((value & 0x07) << 8));
                if (_pulse2Enabled)
                    _pulse2LengthCounter = LengthTable[(value >> 3) & 0x1F];
                _pulse2Sequence = 0;
                _pulse2EnvelopeStart = true;
                break;

            // Triangle
            case 0x4008:
                // Linear counter (simplified)
                break;
            case 0x400A:
                _triangleTimerValue = (ushort)((_triangleTimerValue & 0x700) | value);
                break;
            case 0x400B:
                _triangleTimerValue = (ushort)((_triangleTimerValue & 0x0FF) | ((value & 0x07) << 8));
                if (_triangleEnabled)
                    _triangleLengthCounter = LengthTable[(value >> 3) & 0x1F];
                break;

            // Noise
            case 0x400C:
                _noiseHalt = (value & 0x20) != 0;       // Bit 5: length counter halt / envelope loop
                _noiseConstant = (value & 0x10) != 0;  // Bit 4: constant volume
                _noiseVolume = (byte)(value & 0x0F);
                break;
            case 0x400E:
                _noiseMode = (value & 0x80) != 0;
                _noiseTimerValue = NoisePeriodTable[value & 0x0F];
                break;
            case 0x400F:
                if (_noiseEnabled)
                    _noiseLengthCounter = LengthTable[(value >> 3) & 0x1F];
                _noiseEnvelopeStart = true;
                break;

            // DMC
            case 0x4010:
                _dmcIrqEnabled = (value & 0x80) != 0;
                _dmcLoop = (value & 0x40) != 0;
                _dmcTimerValue = DmcRateTable[value & 0x0F];
                break;
            case 0x4011:
                _dmcOutput = (byte)(value & 0x7F);
                break;
            case 0x4012:
                _dmcSampleAddressStart = (ushort)(0xC000 + (value << 6));
                break;
            case 0x4013:
                _dmcSampleLengthStart = (value << 4) + 1;
                break;

            // Status
            case 0x4015:
                _status = value;
                _pulse1Enabled = (value & 0x01) != 0;
                _pulse2Enabled = (value & 0x02) != 0;
                _triangleEnabled = (value & 0x04) != 0;
                _noiseEnabled = (value & 0x08) != 0;
                _dmcEnabled = (value & 0x10) != 0;
                if (!_pulse1Enabled) _pulse1LengthCounter = 0;
                if (!_pulse2Enabled) _pulse2LengthCounter = 0;
                if (!_triangleEnabled) _triangleLengthCounter = 0;
                if (!_noiseEnabled) _noiseLengthCounter = 0;
                if (_dmcEnabled)
                {
                    // If DMC is enabled and sample length is 0, restart
                    if (_dmcSampleLength == 0)
                    {
                        _dmcSampleAddress = _dmcSampleAddressStart;
                        _dmcSampleLength = _dmcSampleLengthStart;
                    }
                }
                else
                {
                    _dmcSampleLength = 0;
                }
                break;

            // Frame counter
            case 0x4017:
                // Frame counter mode (simplified)
                break;
        }
    }

    public byte CpuRead(ushort addr)
    {
        if (addr == 0x4015)
        {
            byte result = 0;
            if (_pulse1LengthCounter > 0) result |= 0x01;
            if (_pulse2LengthCounter > 0) result |= 0x02;
            if (_triangleLengthCounter > 0) result |= 0x04;
            if (_noiseLengthCounter > 0) result |= 0x08;
            if (_dmcSampleLength > 0) result |= 0x10;
            return result;
        }
        return 0;
    }

    public void Tick(int cpuCycles)
    {
        // Update channel timers
        for (int i = 0; i < cpuCycles; i++)
        {
            // Toggle APU clock (pulse/noise run at half CPU rate)
            _apuClockToggle = !_apuClockToggle;

            if (_apuClockToggle)
            {
                // Pulse 1 (ticks every 2 CPU cycles)
                if (_pulse1Timer > 0)
                    _pulse1Timer--;
                else
                {
                    _pulse1Timer = _pulse1TimerValue;
                    _pulse1Sequence = (_pulse1Sequence + 1) & 0x07;
                }

                // Pulse 2 (ticks every 2 CPU cycles)
                if (_pulse2Timer > 0)
                    _pulse2Timer--;
                else
                {
                    _pulse2Timer = _pulse2TimerValue;
                    _pulse2Sequence = (_pulse2Sequence + 1) & 0x07;
                }

                // Noise (ticks every 2 CPU cycles)
                if (_noiseTimer > 0)
                    _noiseTimer--;
                else
                {
                    _noiseTimer = _noiseTimerValue;
                    int feedback = (_noiseShift & 1) ^ ((_noiseShift >> (_noiseMode ? 6 : 1)) & 1);
                    _noiseShift = (ushort)((_noiseShift >> 1) | (feedback << 14));
                }
            }

            // Triangle (ticks at CPU rate)
            if (_triangleTimer > 0)
                _triangleTimer--;
            else
            {
                _triangleTimer = _triangleTimerValue;
                if (_triangleLengthCounter > 0)
                    _triangleSequence = (_triangleSequence + 1) & 0x1F;
            }

            // DMC (ticks at CPU rate)
            if (_dmcTimer > 0)
                _dmcTimer--;
            else
            {
                _dmcTimer = _dmcTimerValue;
                TickDmc();
            }

            // Frame counter - 4-step sequence (~240Hz)
            _frameCounter++;
            if (_frameCounter >= FrameCounterPeriod)
            {
                _frameCounter = 0;
                _frameStep = (_frameStep + 1) & 3;

                // Clock envelopes every step (~240Hz)
                // The halt flag (bit 5) also serves as envelope loop flag
                ClockEnvelope(ref _pulse1EnvelopeVolume, ref _pulse1EnvelopeDivider, ref _pulse1EnvelopeStart, _pulse1Volume, _pulse1Halt);
                ClockEnvelope(ref _pulse2EnvelopeVolume, ref _pulse2EnvelopeDivider, ref _pulse2EnvelopeStart, _pulse2Volume, _pulse2Halt);
                ClockEnvelope(ref _noiseEnvelopeVolume, ref _noiseEnvelopeDivider, ref _noiseEnvelopeStart, _noiseVolume, _noiseHalt);

                // Steps 1 and 3: clock length counters and sweep (~120Hz)
                if (_frameStep == 1 || _frameStep == 3)
                {
                    // Decrement length counters (halted when halt flag is set)
                    if (_pulse1LengthCounter > 0 && !_pulse1Halt)
                        _pulse1LengthCounter--;
                    if (_pulse2LengthCounter > 0 && !_pulse2Halt)
                        _pulse2LengthCounter--;
                    if (_triangleLengthCounter > 0)
                        _triangleLengthCounter--;
                    if (_noiseLengthCounter > 0 && !_noiseHalt)
                        _noiseLengthCounter--;

                    // Clock sweep units
                    ClockSweep(ref _pulse1TimerValue, ref _pulse1SweepDivider, ref _pulse1SweepReload,
                        _pulse1SweepEnabled, _pulse1SweepPeriod, _pulse1SweepNegate, _pulse1SweepShift, true);
                    ClockSweep(ref _pulse2TimerValue, ref _pulse2SweepDivider, ref _pulse2SweepReload,
                        _pulse2SweepEnabled, _pulse2SweepPeriod, _pulse2SweepNegate, _pulse2SweepShift, false);
                }
            }
        }

        // Generate samples
        _cycleAccumulator += cpuCycles;
        while (_cycleAccumulator >= CyclesPerSample)
        {
            _cycleAccumulator -= CyclesPerSample;
            GenerateSample();
        }
    }

    private void TickDmc()
    {
        if (!_dmcSilence)
        {
            // Output unit: adjust output level based on current bit
            if ((_dmcSampleBuffer & 1) != 0)
            {
                if (_dmcOutput <= 125)
                    _dmcOutput += 2;
            }
            else
            {
                if (_dmcOutput >= 2)
                    _dmcOutput -= 2;
            }
            _dmcSampleBuffer >>= 1;
        }

        _dmcBitsRemaining--;
        if (_dmcBitsRemaining <= 0)
        {
            _dmcBitsRemaining = 8;

            // Try to load next sample byte
            if (_dmcSampleLength > 0 && _dmcReadMemory != null)
            {
                _dmcSampleBuffer = _dmcReadMemory(_dmcSampleAddress);
                _dmcSampleAddress++;
                if (_dmcSampleAddress == 0)
                    _dmcSampleAddress = 0x8000; // Wrap around
                _dmcSampleLength--;
                _dmcSilence = false;

                // If sample ended, check for loop or restart
                if (_dmcSampleLength == 0)
                {
                    if (_dmcLoop)
                    {
                        _dmcSampleAddress = _dmcSampleAddressStart;
                        _dmcSampleLength = _dmcSampleLengthStart;
                    }
                }
            }
            else
            {
                _dmcSilence = true;
            }
        }
    }

    private void GenerateSample()
    {
        float pulse1 = 0;
        float pulse2 = 0;
        float triangle = 0;
        float noise = 0;
        float dmc = _dmcOutput / 127.0f;

        // Pulse 1
        if (_pulse1Enabled && _pulse1LengthCounter > 0 && _pulse1TimerValue >= 8)
        {
            byte duty = DutyTable[_pulse1Duty][_pulse1Sequence];
            byte volume = _pulse1Constant ? _pulse1Volume : _pulse1EnvelopeVolume;
            pulse1 = duty * volume / 15.0f;
        }

        // Pulse 2
        if (_pulse2Enabled && _pulse2LengthCounter > 0 && _pulse2TimerValue >= 8)
        {
            byte duty = DutyTable[_pulse2Duty][_pulse2Sequence];
            byte volume = _pulse2Constant ? _pulse2Volume : _pulse2EnvelopeVolume;
            pulse2 = duty * volume / 15.0f;
        }

        // Triangle
        if (_triangleEnabled && _triangleLengthCounter > 0 && _triangleTimerValue >= 2)
        {
            triangle = TriangleTable[_triangleSequence] / 15.0f;
        }

        // Noise
        if (_noiseEnabled && _noiseLengthCounter > 0)
        {
            int bit = (_noiseShift & 1) ^ 1;
            byte volume = _noiseConstant ? _noiseVolume : _noiseEnvelopeVolume;
            noise = bit * volume / 15.0f;
        }

        // Mix channels (simple linear mix with DMC)
        float pulseOut = 0.00752f * (pulse1 + pulse2) * 15.0f;
        float tndOut = 0.00851f * triangle * 15.0f + 0.00494f * noise * 15.0f + 0.00335f * dmc * 127.0f;
        float sample = pulseOut + tndOut;

        // Clamp
        sample = Math.Clamp(sample, -1.0f, 1.0f);

        if (_sampleIndex < _sampleBuffer.Length)
        {
            _sampleBuffer[_sampleIndex++] = sample;
        }
    }

    public int GetSamples(float[] buffer, int count)
    {
        int available = Math.Min(_sampleIndex, count);
        Array.Copy(_sampleBuffer, 0, buffer, 0, available);

        // Shift remaining samples
        if (available < _sampleIndex)
        {
            Array.Copy(_sampleBuffer, available, _sampleBuffer, 0, _sampleIndex - available);
        }
        _sampleIndex -= available;

        return available;
    }

    public int AvailableSamples => _sampleIndex;

    private void ClockEnvelope(ref byte envelopeVolume, ref byte divider, ref bool start, byte period, bool loop)
    {
        if (start)
        {
            start = false;
            envelopeVolume = 15;
            divider = period;
        }
        else
        {
            if (divider > 0)
            {
                divider--;
            }
            else
            {
                divider = period;
                if (envelopeVolume > 0)
                    envelopeVolume--;
                else if (loop)
                    envelopeVolume = 15;
            }
        }
    }

    private void ClockSweep(ref ushort timerValue, ref byte divider, ref bool reload,
        bool enabled, byte period, bool negate, byte shift, bool isPulse1)
    {
        // Calculate change amount
        int changeAmount = timerValue >> shift;

        // Calculate target period
        // Negate flag: if set, subtract change (pitch goes up); if clear, add change (pitch goes down)
        int targetPeriod;
        if (negate)
        {
            // Pulse 1 uses one's complement (-change - 1), Pulse 2 uses two's complement (-change)
            targetPeriod = timerValue - changeAmount - (isPulse1 ? 1 : 0);
            if (targetPeriod < 0) targetPeriod = 0;
        }
        else
        {
            targetPeriod = timerValue + changeAmount;
        }

        // Check if sweep should update the period
        bool muting = timerValue < 8 || targetPeriod > 0x7FF;

        if (divider == 0 && enabled && !muting && shift > 0)
        {
            timerValue = (ushort)targetPeriod;
        }

        if (divider == 0 || reload)
        {
            divider = period;
            reload = false;
        }
        else
        {
            divider--;
        }
    }
}
