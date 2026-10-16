using TaskMonitor.Core;

internal static class ProgressSelfTest
{
    public static int Run()
    {
        int failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"{(ok ? "OK" : "FAIL")} {name}");
            if (!ok) failed++;
        }
        var p = LogProgressParser.ParseTail("TASK_PROGRESS_JSON {\"done\":12,\"total\":100,\"unit\":\"files\",\"rate\":2,\"subject\":\"batch\",\"stage\":\"convert\",\"eta_seconds\":44}");
        Check(p is { Done: 12, Total: 100, Unit: "files", Rate: 2, Sample: "batch", Stage: "convert", EtaSeconds: 44, Format: "TASK_PROGRESS_JSON" }, "공통 진행 로그 필드");
        Check(LogProgressParser.ParseTail("TASK_PROGRESS_JSON {\"done\":0}") is { Done: 0, Total: null, Unit: "items" }, "총량 없는 작업과 기본 단위");
        foreach (string payload in new[] { "{}", "[]", "{bad}", "{\"done\":true}", "{\"done\":-1}", "{\"done\":\"12\"}",
                    "{\"done\":1,\"total\":0}", "{\"done\":11,\"total\":10}", "{\"done\":1,\"rate\":-1}",
                    "{\"done\":NaN}", "{\"done\":1e999}", "{\"done\":1,\"eta_seconds\":-2}", "{\"done\":1,\"total\":\"10\"}" })
            Check(LogProgressParser.ParseTail("TASK_PROGRESS_JSON " + payload) is null, "잘못된 진행률 무시: " + payload);
        Check(LogProgressParser.ParseTail("ordinary log: converted 12 files") is null, "일반 로그는 진행률로 추측하지 않음");
        Check(LogProgressParser.ParseTail("INFER_PROGRESS_JSON {\"frame\":3,\"total\":10,\"fps\":0.5,\"sample\":\"s\"}")
            is { Done: 3, Total: 10, Rate: 0.5, Unit: "frames", Sample: "s", Format: "INFER_PROGRESS_JSON" }, "기존 추론 로그 호환");
        Check(LogProgressParser.ParseTail("sample:  50%|#####     | 5/10 [00:05<00:05, 1.00it/s]")
            is { Done: 5, Total: 10, Rate: 1, Format: "tqdm" }, "기존 tqdm 호환");
        Check(LogProgressParser.ParseTail("TASK_PROGRESS_JSON {\"done\":1}\rINFER_PROGRESS_JSON {\"frame\":2}\nTASK_PROGRESS_JSON {bad}")
            is { Done: 2, Format: "INFER_PROGRESS_JSON" }, "혼합 로그의 마지막 유효한 진행률");
        Console.WriteLine($"진행 로그 검증: 실패 {failed}개");
        return failed == 0 ? 0 : 1;
    }
}
