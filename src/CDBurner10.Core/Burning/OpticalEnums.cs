namespace CDBurner10.Core.Burning;

/// <summary>
/// Optical media types (flags allow representing drive read/write capability bitmasks).
/// </summary>
[Flags]
public enum OpticalDiscType
{
    None = 0,
    CdRom = 1 << 0,
    CdR = 1 << 1,
    CdRw = 1 << 2,
    DvdRom = 1 << 3,
    DvdR = 1 << 4,
    DvdRw = 1 << 5,
    DvdRDl = 1 << 6,
    DvdPlusR = 1 << 7,
    DvdPlusRw = 1 << 8,
    DvdPlusRDl = 1 << 9,
    DvdRam = 1 << 10,
    BdRom = 1 << 11,
    BdR = 1 << 12,
    BdRDl = 1 << 13,
    BdRe = 1 << 14,
    HdDvdRom = 1 << 15,
    HdDvdR = 1 << 16,
    HdDvdRDl = 1 << 17,
    HdDvdRw = 1 << 18
}

/// <summary>
/// Copy protection system reported by optical media.
/// </summary>
public enum DiscProtectionSystem
{
    None = 0,
    Css = 1
}

/// <summary>
/// El Torito boot image emulation modes.
/// Numeric values match legacy CDBurnerXP .dxp compilation files for backwards compatibility.
/// </summary>
public enum BootEmulationType
{
    NoEmulation = 0,
    Floppy120 = 1,
    Floppy144 = 2,
    Floppy288 = 3,
    HardDisk = 4
}
