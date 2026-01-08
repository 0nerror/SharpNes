using System.IO;

namespace SharpNes.Core;

public interface IMapper
{
    // CPU space: $4020-$FFFF typically mapped through cartridge logic
    bool CpuRead(ushort addr, out byte data);
    bool CpuWrite(ushort addr, byte data);

    // PPU space: $0000-$1FFF for CHR
    bool PpuRead(ushort addr, out byte data);
    bool PpuWrite(ushort addr, byte data);

    // Mirroring mode: 0=one-screen lower, 1=one-screen upper, 2=vertical, 3=horizontal
    // Return -1 to use ROM header mirroring
    int MirrorMode => -1;

    // Save state support
    void SaveState(BinaryWriter writer);
    void LoadState(BinaryReader reader);
}