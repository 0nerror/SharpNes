namespace NesEmu.Core;

/// <summary>
/// Emulates a standard NES controller with serial read protocol.
/// Button order: A, B, Select, Start, Up, Down, Left, Right
/// </summary>
public sealed class NesController
{
    // Button bit positions
    public const int ButtonA = 1;
    public const int ButtonB = 0;
    public const int ButtonSelect = 2;
    public const int ButtonStart = 3;
    public const int ButtonUp = 4;
    public const int ButtonDown = 5;
    public const int ButtonLeft = 6;
    public const int ButtonRight = 7;

    // Current button state (directly updated by input system)
    private byte _buttonState;

    // Latched state for serial reading
    private byte _shiftRegister;

    // Strobe mode - when true, continuously reload shift register
    private bool _strobe;

    /// <summary>
    /// Set or clear a button state.
    /// </summary>
    public void SetButton(int button, bool pressed)
    {
        if (pressed)
            _buttonState |= (byte)(1 << button);
        else
            _buttonState &= (byte)~(1 << button);
    }

    /// <summary>
    /// Write to controller register ($4016 bit 0).
    /// When strobe goes from 1 to 0, the button state is latched.
    /// </summary>
    public void Write(byte value)
    {
        bool newStrobe = (value & 1) != 0;

        if (_strobe && !newStrobe)
        {
            // Strobe going low - latch current button state
            _shiftRegister = _buttonState;
        }

        _strobe = newStrobe;

        if (_strobe)
        {
            // While strobe is high, continuously reload
            _shiftRegister = _buttonState;
        }
    }

    /// <summary>
    /// Read from controller register ($4016 or $4017).
    /// Returns the next button bit (bit 0 of return value).
    /// </summary>
    public byte Read()
    {
        if (_strobe)
        {
            // While strobe is high, always return button A state
            return (byte)(_buttonState & 1);
        }

        // Return current bit and shift
        byte result = (byte)(_shiftRegister & 1);
        _shiftRegister >>= 1;

        // After 8 reads, all subsequent reads return 1 (open bus behavior on real hardware)
        // But we just return 0s since shift register becomes 0
        return result;
    }

    /// <summary>
    /// Get current raw button state (for debugging).
    /// </summary>
    public byte GetState() => _buttonState;
}
