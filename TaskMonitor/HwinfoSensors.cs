using System.IO.MemoryMappedFiles;
using System.Text;

namespace TaskMonitor;

// HWiNFO 센서 공유 메모리 한 칸(온도 또는 팬 등). Kind: 1 온도, 3 팬.
internal sealed record SensorReading(int Kind, string Label, double Value, string Unit);

// HWiNFO의 센서 공유 메모리("Global\HWiNFO_SENS_SM2")를 읽는다. Windows는 드라이버 없이 CPU 온도를 내주지 않아서 센서 도구가 읽은 값을 빌린다(CPU 온도는 AfterburnerSensors도 쓴다).
// HWiNFO를 실행하고 설정에서 "Shared Memory Support"를 켜야 한다. 공개 문서의 구조를 따랐다
// (헤더 'HWiS', 헤더 40바이트 이후 칸 목록, 센서 칸 264바이트, 읽기 칸 316바이트, 구조체는 1바이트 맞춤).
// 이 PC에서 HWiNFO를 켠 상태로 실측하지는 못했다. 자체 검증은 같은 구조로 합성 메모리를 만들어 읽기만 확인한다.
internal static class HwinfoSensors
{
    internal const string DefaultName = "Global\\HWiNFO_SENS_SM2";
    private const uint Signature = 0x53695748; // 'HWiS'
    private const int HeaderReadingOffset = 32;
    private const int HeaderReadingSize = 36;
    private const int HeaderReadingCount = 40;
    private const int ReadingLabelOffset = 12;
    private const int ReadingLabelLength = 128;
    private const int ReadingUnitOffset = 268;
    private const int ReadingUnitLength = 16;
    private const int ReadingValueOffset = 284;

    public const int KindTemperature = 1;
    public const int KindFan = 3;

    // 공유 메모리가 없으면(HWiNFO가 꺼져 있거나 공유 기능이 꺼져 있으면) 빈 목록이다.
    public static IReadOnlyList<SensorReading> Read(string name = DefaultName)
    {
        var readings = new List<SensorReading>();
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Signature)
            {
                return readings;
            }

            uint readingOffset = view.ReadUInt32(HeaderReadingOffset);
            uint readingSize = view.ReadUInt32(HeaderReadingSize);
            uint readingCount = view.ReadUInt32(HeaderReadingCount);
            if (readingSize < ReadingValueOffset + 8 || readingCount > 4096 || readingOffset + (long)readingCount * readingSize > view.Capacity)
            {
                return readings;
            }

            var element = new byte[readingSize];
            for (uint i = 0; i < readingCount; i++)
            {
                view.ReadArray(readingOffset + i * readingSize, element, 0, element.Length);
                int kind = BitConverter.ToInt32(element, 0);
                if (kind != KindTemperature && kind != KindFan)
                {
                    continue;
                }

                string label = Text(element, ReadingLabelOffset, ReadingLabelLength);
                string unit = Text(element, ReadingUnitOffset, ReadingUnitLength);
                double value = BitConverter.ToDouble(element, ReadingValueOffset);
                readings.Add(new SensorReading(kind, label, value, unit));
            }
        }
        catch (FileNotFoundException)
        {
            // HWiNFO가 실행 중이 아니다.
        }
        catch (UnauthorizedAccessException)
        {
            // 권한이 없거나 공유 기능이 막혔다.
        }

        return readings;
    }

    // CPU 온도: 패키지·다이 온도 중 가장 높은 값(라벨이 CPU이고 Package·Tctl·Die 중 하나를 포함하는 온도).
    public static double? CpuTemperatureC(IReadOnlyList<SensorReading> readings)
    {
        var values = readings
            .Where(r => r.Kind == KindTemperature && r.Unit.Contains('C') && r.Label.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                && (r.Label.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || r.Label.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                    || r.Label.Contains("Die", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Value)
            .ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    private static string Text(byte[] buffer, int offset, int length)
    {
        int end = Array.IndexOf(buffer, (byte)0, offset, length);
        int count = end < 0 ? length : end - offset;
        return Encoding.Default.GetString(buffer, offset, count).Trim();
    }
}
