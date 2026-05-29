namespace HiveDB.Storage;

internal enum PageType : byte
{
    Free = 0x00,
    Header = 0x01,
    Key = 0x02,
    Overflow = 0x03,
}
