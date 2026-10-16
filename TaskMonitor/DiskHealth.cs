using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskMonitor;

// 디스크 건강 정보: 장치 정보(모델, 일련번호, 인터페이스)와 상태 값(온도, 남은 수명, 사용 시간, SMART 항목).
// NVMe: 장치 질의(IOCTL_STORAGE_QUERY_PROPERTY)로 상태 로그 페이지(로그 ID 2)를 읽는다. 관리자 권한 없이 시도한다.
// SATA·USB: ATA 명령(SMART READ DATA, SMART READ THRESHOLDS)을 IOCTL_ATA_PASS_THROUGH로 보낸다. 관리자 권한이 필요하다.
// 접근 방식은 CrystalDiskInfo(MIT 라이선스, hiyohiyo)를 참고했다. 코드는 새로 작성했다.

internal sealed record DiskIdentity(string Model, string Serial, string Firmware, string Bus);

// Id: ATA 속성 번호(NVMe는 "-"). Current·Worst·Threshold: 정규화 값. Raw: 사람이 읽을 원시 값. Bad: 경고 여부.
internal sealed record SmartAttribute(string Id, string Name, string Current, string Worst, string Threshold, string Raw, bool Bad);

internal sealed record DiskHealthInfo(
    int Number,
    DiskIdentity Identity,
    string Verdict,
    string Note,
    int? TemperatureC,
    int? LifeLeftPercent,
    long? PowerOnHours,
    long? PowerCycles,
    long? HostReadBytes,
    long? HostWriteBytes,
    IReadOnlyList<SmartAttribute> Attributes);

internal static class DiskHealth
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlAtaPassThrough = 0x0004D02C;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const int LogLength = 512;
    private const int AtaTableLength = 362;
    private const int AtaEntryLength = 12;
    private const int MaxDiskNumber = 16;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    // 물리 디스크 0~15번을 차례로 읽는다. 없는 번호는 바로 실패하므로 금방 끝난다.
    public static IReadOnlyDictionary<int, DiskHealthInfo> ReadAll()
    {
        var result = new Dictionary<int, DiskHealthInfo>();
        for (int number = 0; number <= MaxDiskNumber; number++)
        {
            var info = Read(number);
            if (info is not null)
            {
                result[number] = info;
            }
        }

        return result;
    }

    public static DiskHealthInfo? Read(int number)
    {
        IntPtr handle = Open(number, 0, out _);
        if (handle == InvalidHandle)
        {
            return null;
        }

        try
        {
            var identity = QueryIdentity(handle);
            if (identity is null)
            {
                return null;
            }

            return identity.Bus == "NVMe" ? ReadNvme(number, handle, identity) : ReadAta(number, identity);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    // NVMe 상태 로그 해석. 바이트 위치는 NVMe 사양의 SMART/Health 정보 로그(로그 ID 2)를 따른다.
    internal static DiskHealthInfo ParseNvme(int number, DiskIdentity identity, byte[] log)
    {
        int critical = log[0];
        int temperatureKelvin = BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(1));
        int spare = log[3];
        int spareThreshold = log[4];
        int used = log[5];
        long read = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(32)) * 512000;   // 데이터 단위 = 1000 × 512바이트
        long written = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(48)) * 512000;
        long cycles = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(112));
        long hours = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(128));
        long unsafeShutdowns = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(144));
        long mediaErrors = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(160));
        long errorLogEntries = (long)BinaryPrimitives.ReadUInt64LittleEndian(log.AsSpan(176));

        int? temperature = temperatureKelvin > 0 ? temperatureKelvin - 273 : null;
        int lifeLeft = Math.Clamp(100 - used, 0, 100);

        string verdict = critical != 0 || used >= 100 ? "나쁨"
            : used >= 90 || mediaErrors > 0 || temperature is >= 70 ? "주의"
            : "양호";

        var attributes = new List<SmartAttribute>
        {
            new("-", "중요 경고", "-", "-", "-", $"0x{critical:X2}", critical != 0),
            new("-", "온도", "-", "-", "-", temperature is { } t ? $"{t} °C" : "-", temperature is >= 70),
            new("-", "남은 여분 용량", $"{spare}%", "-", $"{spareThreshold}%", "", spare < spareThreshold),
            new("-", "사용량", "-", "-", "-", $"{used}%", used >= 90),
            new("-", "읽은 데이터 합계", "-", "-", "-", FormatBytes(read), false),
            new("-", "쓴 데이터 합계", "-", "-", "-", FormatBytes(written), false),
            new("-", "전원 켬 횟수", "-", "-", "-", $"{cycles:N0}회", false),
            new("-", "사용 시간", "-", "-", "-", $"{hours:N0}시간", false),
            new("-", "비정상 종료", "-", "-", "-", $"{unsafeShutdowns:N0}회", unsafeShutdowns > 0),
            new("-", "미디어 오류", "-", "-", "-", $"{mediaErrors:N0}회", mediaErrors > 0),
            new("-", "오류 로그 항목", "-", "-", "-", $"{errorLogEntries:N0}개", false),
        };

        return new DiskHealthInfo(number, identity, verdict, "", temperature, lifeLeft, hours, cycles, read, written, attributes);
    }

    // ATA SMART 해석. 속성 표는 2바이트 머리말 뒤에 12바이트씩 30칸이다. 임계값 표도 같은 배치다.
    internal static DiskHealthInfo ParseAta(int number, DiskIdentity identity, byte[] smart, byte[]? thresholds)
    {
        var limits = new Dictionary<int, int>();
        if (thresholds is not null)
        {
            for (int i = 2; i + AtaEntryLength <= AtaTableLength; i += AtaEntryLength)
            {
                if (thresholds[i] != 0)
                {
                    limits[thresholds[i]] = thresholds[i + 1];
                }
            }
        }

        var attributes = new List<SmartAttribute>();
        var currents = new Dictionary<int, int>();
        var raws = new Dictionary<int, long>();
        for (int i = 2; i + AtaEntryLength <= AtaTableLength; i += AtaEntryLength)
        {
            int id = smart[i];
            if (id == 0)
            {
                continue;
            }

            int current = smart[i + 3];
            int worst = smart[i + 4];
            long raw = ReadRaw48(smart, i + 5);
            int threshold = limits.GetValueOrDefault(id);
            bool bad = threshold > 0 && current <= threshold;
            attributes.Add(new SmartAttribute(
                id.ToString(), AtaName(id), current.ToString(), worst.ToString(), threshold.ToString(), AtaRaw(id, raw), bad));
            currents[id] = current;
            raws[id] = raw;
        }

        int? temperature = raws.TryGetValue(194, out long t194) ? (int)(t194 & 0xFF)
            : raws.TryGetValue(190, out long t190) ? (int)(t190 & 0xFF)
            : null;
        int? lifeLeft = currents.TryGetValue(231, out int life) ? life : null;
        long? hours = raws.TryGetValue(9, out long poh) ? poh : null;
        long? cycles = raws.TryGetValue(12, out long pc) ? pc : null;

        bool warn = raws.GetValueOrDefault(5) > 0 || raws.GetValueOrDefault(197) > 0 || raws.GetValueOrDefault(198) > 0 || temperature is >= 70;
        string verdict = attributes.Any(a => a.Bad) ? "나쁨" : warn ? "주의" : "양호";

        return new DiskHealthInfo(number, identity, verdict, "", temperature, lifeLeft, hours, cycles, null, null, attributes);
    }

    internal static string FormatBytes(long bytes)
    {
        // 화면의 다른 용량 표시와 맞춰 2진 단위(1GB = 1024MB)를 쓴다.
        return bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):F2} TB"
            : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
            : $"{bytes / (double)(1L << 20):F0} MB";
    }

    private static DiskHealthInfo ReadNvme(int number, IntPtr handle, DiskIdentity identity)
    {
        var log = NvmeLog(handle, 2, out int error);
        if (log is null)
        {
            return new DiskHealthInfo(number, identity, "확인 불가", $"NVMe 상태 로그를 읽지 못했습니다(오류 {error}).",
                null, null, null, null, null, null, Array.Empty<SmartAttribute>());
        }

        return ParseNvme(number, identity, log);
    }

    private static DiskHealthInfo ReadAta(int number, DiskIdentity identity)
    {
        const int accessDenied = 5;
        IntPtr handle = Open(number, GenericRead | GenericWrite, out int openError);
        if (handle == InvalidHandle)
        {
            string note = openError == accessDenied
                ? "SATA·USB 장치의 SMART 값은 관리자 권한으로 실행해야 읽을 수 있습니다."
                : $"장치를 열지 못했습니다(오류 {openError}).";
            return new DiskHealthInfo(number, identity, "확인 불가", note,
                null, null, null, null, null, null, Array.Empty<SmartAttribute>());
        }

        try
        {
            var smart = AtaCommand(handle, 0xD0, out int error);
            if (smart is null)
            {
                return new DiskHealthInfo(number, identity, "확인 불가", $"SMART 명령이 실패했습니다(오류 {error}). USB 어댑터는 이 명령을 지원하지 않을 수 있습니다.",
                    null, null, null, null, null, null, Array.Empty<SmartAttribute>());
            }

            var thresholds = AtaCommand(handle, 0xD1, out _);
            return ParseAta(number, identity, smart, thresholds);
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    private static IntPtr Open(int number, uint access, out int error)
    {
        IntPtr handle = CreateFileW($@"\\.\PhysicalDrive{number}", access, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        error = handle == InvalidHandle ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    private static DiskIdentity? QueryIdentity(IntPtr handle)
    {
        var query = new byte[12];   // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery
        var data = Control(handle, IoctlStorageQueryProperty, query, 1024, out _);
        if (data is null)
        {
            return null;
        }

        uint busType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        return new DiskIdentity(
            StringAt(data, 16),   // ProductIdOffset
            StringAt(data, 24),   // SerialNumberOffset
            StringAt(data, 20),   // ProductRevisionOffset
            BusName(busType));
    }

    // STORAGE_PROTOCOL_SPECIFIC_DATA(NVMe, 로그 페이지)를 질의하고, 응답에서 512바이트 로그를 꺼낸다.
    private static byte[]? NvmeLog(IntPtr handle, uint logId, out int error)
    {
        const int headerLength = 8;
        const int protocolDataLength = 40;
        var query = new byte[headerLength + protocolDataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(0), 49);             // StorageDeviceProtocolSpecificProperty
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(4), 0);              // PropertyStandardQuery
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(8), 3);              // ProtocolTypeNvme
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(12), 2);             // NVMeDataTypeLogPage
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(16), logId);         // 로그 페이지 ID
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(20), 0);             // 오프셋 하위 32비트
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(24), protocolDataLength); // 데이터는 구조체 뒤에 온다
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(28), LogLength);

        var data = Control(handle, IoctlStorageQueryProperty, query, headerLength + protocolDataLength + LogLength + 64, out error);
        if (data is null)
        {
            return null;
        }

        // STORAGE_PROTOCOL_DATA_DESCRIPTOR: 머리말 8바이트 뒤에 STORAGE_PROTOCOL_SPECIFIC_DATA가 있고,
        // 그 안의 ProtocolDataOffset부터 실제 로그가 시작한다.
        int start = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + 16));
        if (start + LogLength > data.Length)
        {
            error = 0;
            return null;
        }

        return data[start..(start + LogLength)];
    }

    // ATA 명령 하나를 보낸다. 입출력 버퍼는 ATA_PASS_THROUGH_EX(48바이트) 뒤에 512바이트 데이터가 붙는다.
    private static byte[]? AtaCommand(IntPtr handle, byte feature, out int error)
    {
        const int structLength = 48;
        var buffer = new byte[structLength + LogLength];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0), structLength);  // Length
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), 0x0002);        // AtaFlags: ATA_FLAGS_DATA_IN
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), LogLength);     // DataTransferLength
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), 10);           // TimeOutValue(초)
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), structLength); // DataBufferOffset
        buffer[40] = feature;   // CurrentTaskFile: Features
        buffer[41] = 1;         // Sector count
        buffer[42] = 1;         // LBA low
        buffer[43] = 0x4F;      // LBA mid (SMART 서명)
        buffer[44] = 0xC2;      // LBA high (SMART 서명)
        buffer[45] = 0xA0;      // Device
        buffer[46] = 0xB0;      // Command: SMART

        bool ok = DeviceIoControl(handle, IoctlAtaPassThrough, buffer, (uint)buffer.Length, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok ? buffer[structLength..] : null;
    }

    private static byte[]? Control(IntPtr handle, uint code, byte[] input, int outputSize, out int error)
    {
        var output = new byte[outputSize];
        bool ok = DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)outputSize, out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok ? output : null;
    }

    private static string StringAt(byte[] data, int fieldOffset)
    {
        int start = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fieldOffset));
        if (start <= 0 || start >= data.Length)
        {
            return "";
        }

        int end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, start, end - start).Trim();
    }

    private static long ReadRaw48(byte[] buffer, int offset)
    {
        long value = 0;
        for (int k = 0; k < 6; k++)
        {
            value |= (long)buffer[offset + k] << (8 * k);
        }

        return value;
    }

    private static string AtaRaw(int id, long raw) => id switch
    {
        194 or 190 => $"{raw & 0xFF} °C",
        9 => $"{raw:N0}시간",
        _ => raw.ToString("N0"),
    };

    private static string AtaName(int id) => id switch
    {
        1 => "읽기 오류율",
        3 => "스핀업 시간",
        4 => "전원 켜고 끈 횟수",
        5 => "재할당 섹터 수",
        9 => "사용 시간",
        10 => "스핀 재시도 횟수",
        12 => "전원 켬 횟수",
        170 => "예비 공간",
        171 => "프로그램 실패 횟수",
        172 => "지우기 실패 횟수",
        173 => "마모 평준화 횟수",
        174 => "예기치 않은 전원 차단",
        177 => "마모 범위 차이",
        179 => "사용된 예비 블록",
        181 => "프로그램 실패 총계",
        182 => "지우기 실패 총계",
        187 => "보고된 복구 불가 오류",
        188 => "명령 시간 초과",
        190 => "공기 흐름 온도",
        194 => "온도",
        195 => "ECC 오류",
        196 => "재할당 이벤트",
        197 => "대기 중 불안정 섹터",
        198 => "복구 불가 섹터",
        199 => "UDMA CRC 오류",
        231 => "남은 SSD 수명",
        233 => "미디어 마모 지표",
        241 => "총 쓴 LBA",
        242 => "총 읽은 LBA",
        _ => $"속성 {id}",
    };

    private static string BusName(uint bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        7 => "USB",
        10 => "SAS",
        11 => "SATA",
        14 => "가상",
        17 => "NVMe",
        _ => $"기타({bus})",
    };
}

// 디스크 건강 정보를 주기적으로 읽는 백그라운드 스레드. 화면은 마지막으로 읽은 값만 쓰므로 기다리지 않는다.
internal static class DiskHealthMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static IReadOnlyDictionary<int, DiskHealthInfo> _latest = new Dictionary<int, DiskHealthInfo>();
    private static int _started;

    public static IReadOnlyDictionary<int, DiskHealthInfo> Latest => Volatile.Read(ref _latest);

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    Volatile.Write(ref _latest, DiskHealth.ReadAll());
                }
                catch (Exception)
                {
                    // 한 번 실패해도 다음 주기에 다시 읽는다.
                }

                Thread.Sleep(Interval);
            }
        })
        {
            IsBackground = true,
            Name = "TaskMonitor disk health",
        };
        thread.Start();
    }
}
