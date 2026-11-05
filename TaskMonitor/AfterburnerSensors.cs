using System.IO.MemoryMappedFiles;

namespace TaskMonitor;

// MSI Afterburner 하드웨어 모니터 공유 메모리("MAHMSharedMemory")에서 CPU 온도를 읽는다.
// Afterburner가 자기 드라이버로 읽은 값을 내놓으므로 이 앱은 드라이버도 관리자 권한도 필요 없다.
// 구조는 Afterburner SDK의 MAHMSharedMemory.h v2.0을 따랐다(헤더: 서명 'MAHM', 버전, 헤더 크기, 칸 수, 칸 크기).
// 칸: 이름 등 문자열 260바이트 5개 뒤에 float 값(1300), 칸 크기가 1324 이상이면 1320에 원천 ID가 있다.
// 이 PC(Ryzen 9 7950X)에서 Afterburner 실행 중 "CPU temperature" 43.6°C를 실측해 맞췄다.
internal static class AfterburnerSensors
{
    internal const string DefaultName = "MAHMSharedMemory";
    private const uint Signature = 0x4D41484D; // 'MAHM'
    private const int NameLength = 260;
    private const int ValueOffset = 1300;
    private const int SourceIdOffset = 1320;
    private const uint SourceCpuTemperature = 0x80;

    // Afterburner가 꺼져 있거나 CPU 온도 칸이 없으면 null이다. 코어별 칸과 전체 칸 중 가장 높은 값을 쓴다.
    public static double? CpuTemperatureC(string name = DefaultName)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Signature)
            {
                return null;
            }

            uint headerSize = view.ReadUInt32(8);
            uint count = view.ReadUInt32(12);
            uint entrySize = view.ReadUInt32(16);
            if (entrySize < ValueOffset + 4 || count > 4096 || headerSize < 20 || headerSize + (long)count * entrySize > view.Capacity)
            {
                return null;
            }

            double? best = null;
            var label = new byte[NameLength];
            for (uint i = 0; i < count; i++)
            {
                long at = headerSize + (long)i * entrySize;
                // 원천 ID가 없는 옛 형식은 영어 원본 이름("CPU temperature", "CPU1 temperature")으로 고른다.
                bool isCpuTemp = entrySize >= SourceIdOffset + 4
                    ? view.ReadUInt32(at + SourceIdOffset) == SourceCpuTemperature
                    : ReadLabel(view, at, label) is var text && text.StartsWith("CPU", StringComparison.Ordinal) && text.EndsWith("temperature", StringComparison.Ordinal);
                if (!isCpuTemp)
                {
                    continue;
                }

                // 값이 없으면 Afterburner는 FLT_MAX를 넣는다.
                float value = view.ReadSingle(at + ValueOffset);
                if (value is > 0 and < 150 && (best is null || value > best))
                {
                    best = value;
                }
            }

            return best;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string ReadLabel(MemoryMappedViewAccessor view, long at, byte[] buffer)
    {
        view.ReadArray(at, buffer, 0, buffer.Length);
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
