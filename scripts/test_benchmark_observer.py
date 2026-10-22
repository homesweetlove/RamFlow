"""Deterministic standard-library tests; no live Windows calls or launched processes.

Run on Linux or Windows: python -m unittest discover -s scripts -p test_benchmark_observer.py -v
"""
import contextlib
import csv
import ctypes as C
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location("benchmark_observer", Path(__file__).with_name("benchmark-observer.py"))
observer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(observer)


class Clock:
    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now


class Backend:
    pdh_reason = None

    def __init__(self, clock):
        self.clock = clock
        self.scans = []
        self.settings_reply = (123, {"DryRun": True, "Paused": False})
        self.lower_priority = Mock(return_value=True)
        self.close_workload = Mock()
        self.workload_handle = Mock(return_value=567)
        self.workload_completion = Mock(return_value=(1.5, True))
        self.close = Mock()

    def ramflow_processes(self):
        return self.scans.pop(0) if self.scans else []

    def settings(self, name):
        if isinstance(self.settings_reply, Exception):
            raise self.settings_reply
        return self.settings_reply

    def memory(self):
        return dict(memory_load_percent=30, physical_available_bytes=1000)

    def system_times(self):
        t = int(self.clock() * 10000000)
        return t // 2, t, t

    def pages_input(self):
        return None

    def process(self, pid):
        return dict(identity=pid * 10, cpu_seconds=self.clock() * .1,
                    working_set_bytes=200, private_bytes=300, status="ok")


class Control:
    def __init__(self, clock, stop=1e6, hook=None):
        self.clock, self.stop, self.hook = clock, stop, hook
        self.start = Mock()

    def wait(self, seconds):
        self.clock.now += seconds
        if self.hook:
            self.hook()
        return self.clock.now >= self.stop


def args(*extra):
    return observer.parse_args(["--condition", "absent", "--measurement", "off",
                                "--output", "unused", *extra])


class ObserverTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        self.backend = Backend(self.clock)

    def run_observer(self, options=None, control=None, launcher=None):
        return observer.observe(options or args("--duration", "4"), self.backend,
                                control or Control(self.clock), self.clock,
                                launcher or Mock(side_effect=AssertionError("No launch expected")))

    def assert_bad_args(self, *extra):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            args(*extra)
        self.assertEqual(caught.exception.code, 2)

    def test_argument_limits_and_nonfinite(self):
        for option, value in [("--sample-seconds", "1.99"), ("--sample-seconds", "nan"),
                              ("--sample-seconds", "inf"), ("--duration", "-1"),
                              ("--duration", "nan"), ("--duration", "inf")]:
            with self.subTest(option=option, value=value):
                self.assert_bad_args(option, value)
        self.assertEqual(args("--sample-seconds", "2", "--duration", "0").duration, 0)

    def test_pid_limits_deduplication_and_remainder(self):
        a = args("--condition", "dry-run", "--ramflow-pid", "123", "123",
                 "--ramflow-pid", "456", "--workload", "tool.exe", "--duration", "99")
        self.assertEqual(a.ramflow_pid, [123, 456])
        self.assertEqual(a.workload, ["tool.exe", "--duration", "99"])
        self.assertIsNone(a.duration)
        for value in ("0", "-1", "4294967296"):
            self.assert_bad_args("--condition", "dry-run", "--ramflow-pid", value)
        self.assert_bad_args("--condition", "dry-run", "--ramflow-pid",
                             *map(str, range(1, observer.MAX_PIDS + 2)))
        self.assert_bad_args("--workload")

    def test_absent_rejects_pid_and_pipe_options(self):
        self.assert_bad_args("--ramflow-pid", "123")
        self.assert_bad_args("--settings-pipe", "RamFlow.Native.test")

    def test_intervention_requires_acknowledgement(self):
        self.assert_bad_args("--condition", "intervention")
        self.assertEqual(args("--condition", "intervention", "--acknowledge-existing-intervention").condition,
                         "intervention")

    def test_pipe_path_validation(self):
        for value in (r"\\server\pipe\RamFlow.Native.x", "unrelated", "RamFlow.Native.a/b", "RamFlow.Native." + "x" * 241):
            self.assert_bad_args("--condition", "dry-run", "--settings-pipe", value)

    def test_absence_rejection_at_startup_does_not_start_or_write(self):
        for detected in ([{"pid": 42, "name": "RamFlow.Service.exe"}], None):
            self.backend.scans = [detected]
            control = Control(self.clock)
            with patch.object(observer, "write_report") as writer, self.assertRaises(observer.Rejected):
                self.run_observer(control=control)
            control.start.assert_not_called()
            self.backend.lower_priority.assert_not_called()
            writer.assert_not_called()

    def test_absence_recheck_after_manual_wait(self):
        self.backend.scans = [[], [{"pid": 42, "name": "RamFlow.exe"}]]
        with self.assertRaises(observer.Rejected):
            self.run_observer()

    def test_absence_detection_during_run_invalidates_even_if_end_clear(self):
        self.backend.scans = [[], [], [], [{"pid": 42, "name": "RamFlow.exe"}], []]
        report = self.run_observer(args("--duration", "10"))
        self.assertEqual(report["status"], "rejected")
        self.assertEqual(report["condition_evidence"]["label"], "REJECTED")
        self.assertEqual(report["condition_evidence"]["last"]["phase"], "end")

    def test_absence_detection_end_and_unavailable_during(self):
        for last in ([{"pid": 42, "name": "RamFlow.exe"}], None):
            self.clock.now = 0
            self.backend.scans = [[], [], [], last]
            report = self.run_observer(args("--duration", "1"))
            self.assertEqual(report["status"], "rejected")

    def test_dryrun_no_pipe_and_denied_are_unverified(self):
        for pipe in ([], ["--settings-pipe", "RamFlow.Native.x"]):
            self.clock.now = 0
            self.backend.settings_reply = PermissionError("denied")
            report = self.run_observer(args("--condition", "dry-run", "--duration", "1", *pipe))
            self.assertEqual(report["condition_evidence"]["label"], "USER DECLARED / unverified")
            self.assertEqual(report["measurement"]["label"], "USER DECLARED / unverified")

    def test_pipe_settings_verification_is_scoped_and_measurement_never_verified(self):
        report = self.run_observer(args("--condition", "dry-run", "--measurement", "on",
                                       "--duration", "1", "--settings-pipe", "RamFlow.Native.x",
                                       "--ramflow-pid", "123", "456"))
        evidence = report["condition_evidence"]
        self.assertEqual(evidence["label"], "VERIFIED at sampled checkpoints only")
        self.assertEqual(evidence["first"]["server_pid"], 123)
        self.assertIn("gaps", evidence["scope"])
        self.assertEqual(report["measurement"]["label"], "USER DECLARED / unverified")

    def test_malformed_pipe_fields_never_verify(self):
        for settings in ({}, {"DryRun": "true", "Paused": False}, {"DryRun": True}):
            self.backend.settings_reply = (123, settings)
            check = observer.Verification(args("--condition", "dry-run", "--settings-pipe",
                                               "RamFlow.Native.x", "--ramflow-pid", "123"), self.backend)
            check.check("test")
            self.assertEqual(check.export()["label"], "USER DECLARED / unverified")

    def test_conflicting_mode_pause_or_server_rejected(self):
        for reply in ((123, {"DryRun": False, "Paused": False}),
                      (123, {"DryRun": True, "Paused": True}),
                      (999, {"DryRun": True, "Paused": False})):
            self.backend.settings_reply = reply
            check = observer.Verification(args("--condition", "dry-run", "--settings-pipe",
                                               "RamFlow.Native.x", "--ramflow-pid", "123"), self.backend)
            with self.assertRaises(observer.Rejected):
                check.check("test")

    def test_intervention_observes_existing_mode_only(self):
        self.backend.settings_reply = (123, {"DryRun": False, "Paused": False})
        report = self.run_observer(args("--condition", "intervention", "--acknowledge-existing-intervention",
                                       "--duration", "1", "--settings-pipe", "RamFlow.Native.x",
                                       "--ramflow-pid", "123"))
        self.assertEqual(report["condition_evidence"]["unverified_checkpoints"], 0)
        self.backend.lower_priority.assert_called_once_with()

    def test_missing_counters_are_null_not_zero(self):
        self.backend.process = Mock(return_value={"status": "unavailable"})
        self.backend.memory = Mock(return_value={"memory_load_percent": None})
        self.backend.system_times = Mock(return_value=None)
        report = self.run_observer()
        for metric in ("observer.cpu_seconds", "observer.cpu_percent_one_core", "observer.private_bytes",
                       "memory_load_percent", "system_cpu_percent", "pages_input_per_second"):
            self.assertIsNone(report["metrics"][metric]["mean"])
            self.assertGreater(report["metrics"][metric]["missing"], 0)
        self.assertIsNone(report["observer"]["endpoint_cpu_delta_seconds"])

    def test_valid_zero_and_online_variance(self):
        aggregate = observer.Aggregate()
        for value in (0, 2, None, float("nan"), float("inf")):
            aggregate.add(value)
        result = aggregate.export()
        self.assertEqual(result["count"], 2)
        self.assertEqual(result["missing"], 3)
        self.assertEqual(result["min"], 0)
        self.assertEqual(result["mean"], 1)
        self.assertAlmostEqual(result["sample_stdev"], 2 ** .5)

    def test_cpu_deltas_and_pid_identity(self):
        sampler = observer.Sampler(self.backend, [("observer", 123)], self.clock)
        self.assertIsNone(sampler.sample()["system_cpu_percent"])
        self.clock.now = 2
        values = sampler.sample()
        self.assertAlmostEqual(values["system_cpu_percent"], 75)
        self.assertAlmostEqual(values["observer.cpu_percent_one_core"], 10)
        self.backend.process = Mock(return_value=dict(identity=999, cpu_seconds=4))
        self.clock.now = 4
        self.assertIsNone(sampler.sample()["observer.cpu_percent_one_core"])

    def test_load_and_sampling_cost_backoff(self):
        self.assertEqual(observer.interval_for({"system_cpu_percent": 90}, 0, 2), 10)
        self.assertEqual(observer.interval_for({"memory_load_percent": 95}, 0, 2), 10)
        self.assertEqual(observer.interval_for({}, .5, 2), 10)
        self.assertEqual(observer.interval_for({}, 3, 2), 30)
        self.assertEqual(observer.interval_for({}, 0, 2), 2)
        self.backend.memory = Mock(return_value={"memory_load_percent": 95})
        report = self.run_observer(args("--duration", "12"))
        self.assertEqual(report["observer"]["samples"], 3)  # start, 10 seconds, end
        self.assertEqual(report["observer"]["load_backoffs"], 2)
        self.assertEqual(report["observer"]["sample_gaps_seconds"]["max"], 10)

    def test_endpoint_only_does_not_periodically_sample_or_check(self):
        report = self.run_observer(args("--duration", "0"), Control(self.clock, stop=40))
        self.assertEqual(report["observer"]["samples"], 2)
        self.assertTrue(report["observer"]["endpoint_only"])
        self.assertEqual(report["condition_evidence"]["checkpoints"], 4)  # startup/start/initial/end
        self.assertEqual(report["elapsed_seconds"], 40)

    def test_constant_storage_with_many_samples(self):
        sampler = observer.Sampler(self.backend, [("observer", 123)], self.clock)
        sampler.sample()
        keys = set(sampler.aggregates)
        for step in range(1, 3001):
            self.clock.now = step * 2
            sampler.sample()
        self.assertEqual(set(sampler.aggregates), keys)
        self.assertEqual(sampler.samples, 3001)
        self.assertEqual(len(sampler.previous_process), 1)
        self.assertEqual(sampler.aggregates["observer.cpu_seconds"].count, 3001)
        self.assertEqual(len(sampler.pids), 1)  # explicit bounded configuration, not a sample history
        self.assertEqual(len(sampler.first["metrics"]), len(keys))
        self.assertEqual(len(sampler.last["metrics"]), len(keys))
        for obj in sampler.aggregates.values():
            self.assertFalse(any(isinstance(v, list) for v in vars(obj).values()))

    def test_workload_running_at_stop_stays_null_even_if_exits_during_finalization(self):
        process = Mock(pid=555)
        process.poll.return_value = None
        original = self.backend.ramflow_processes
        calls = 0

        def scan():
            nonlocal calls
            calls += 1
            if calls == 4:
                process.poll.return_value = 0  # exits after stop, during end check
            return original()

        self.backend.ramflow_processes = scan
        report = self.run_observer(args("--duration", "1", "--workload", "test.exe"),
                                   launcher=Mock(return_value=process))
        self.assertIsNone(report["workload"]["completion_duration_seconds"])
        self.assertTrue(report["workload"]["still_running"])
        self.backend.workload_completion.assert_not_called()
        process.terminate.assert_not_called()
        process.kill.assert_not_called()
        process.wait.assert_not_called()

    def test_completed_workload_has_os_duration(self):
        process = Mock(pid=555)
        process.poll.return_value = 0
        report = self.run_observer(args("--duration", "1", "--workload", "test.exe"),
                                   launcher=Mock(return_value=process))
        self.assertEqual(report["workload"]["completion_duration_seconds"], 1.5)
        self.backend.close_workload.assert_called_once_with(567)

    def test_workload_exit_racing_stop_poll_is_not_completed(self):
        process = Mock(pid=555)
        process.poll.return_value = 0  # poll sees an exit just AFTER the cutoff
        self.backend.workload_completion.return_value = (None, False)
        report = self.run_observer(args("--duration", "1", "--workload", "test.exe"),
                                   launcher=Mock(return_value=process))
        self.assertIsNone(report["workload"]["completion_duration_seconds"])
        self.assertFalse(report["workload"]["completed_by_stop"])
        self.assertTrue(report["workload"]["still_running"])
        self.assertIsNone(report["workload"]["exit_code"])

    def test_final_endpoint_precedes_slow_end_verification(self):
        original = self.backend.ramflow_processes
        calls = 0

        def slow_end():
            nonlocal calls
            calls += 1
            if calls == 4:
                self.clock.now += 5
            return original()

        self.backend.ramflow_processes = slow_end
        report = self.run_observer(args("--duration", "1"))
        self.assertEqual(report["elapsed_seconds"], 1)
        self.assertEqual(report["observer"]["endpoint_span_seconds"], 1)
        self.assertEqual(report["endpoints"]["last"]["monotonic_seconds"], 1)
        self.assertEqual(self.clock(), 6)

    def test_launch_uses_explicit_normal_priority_and_no_shell(self):
        with patch.object(observer.subprocess, "Popen") as popen:
            observer.launch_workload(["program.exe", "literal argument"])
        popen.assert_called_once_with(["program.exe", "literal argument"], shell=False,
                                      stdin=observer.subprocess.DEVNULL, creationflags=0x220)

    def test_interrupt_leaves_workload_running_and_reports_after_stop(self):
        control = Control(self.clock)
        control.wait = Mock(side_effect=KeyboardInterrupt)
        process = Mock(pid=555)
        process.poll.return_value = None
        with patch.object(observer, "write_report") as writer:
            report = self.run_observer(args("--duration", "5", "--workload", "test.exe"),
                                       control, Mock(return_value=process))
        self.assertEqual(report["stop_reason"], "interrupt")
        process.kill.assert_not_called()
        writer.assert_not_called()

    def test_output_csv_nulls_and_bounded_aggregate_schema(self):
        report = self.run_observer()
        # In-memory streams keep this deterministic under restricted filesystem sandboxes.
        streams = [io.StringIO(), io.StringIO()]
        contexts = [contextlib.nullcontext(stream) for stream in streams]
        with patch.object(observer.Path, "mkdir"), patch.object(observer.Path, "open", side_effect=contexts):
            observer.write_report("unused/trial", report)
        saved = json.loads(streams[0].getvalue())
        rows = list(csv.DictReader(io.StringIO(streams[1].getvalue())))
        self.assertEqual(saved["measurement"]["label"], "USER DECLARED / unverified")
        self.assertEqual(len(rows), len(report["metrics"]))
        missing = next(row for row in rows if row["metric"] == "pages_input_per_second")
        self.assertEqual(missing["mean"], "")

    def test_pipe_contradiction_without_pid_still_rejects(self):
        self.backend.settings_reply = (123, {"DryRun": False, "Paused": False})
        verification = observer.Verification(args("--condition", "dry-run", "--settings-pipe",
                                                  "RamFlow.Native.x"), self.backend)
        with self.assertRaises(observer.Rejected):
            verification.check("start")

    def test_intermediate_unverified_reason_is_retained(self):
        verification = observer.Verification(args("--condition", "dry-run", "--settings-pipe",
                                                  "RamFlow.Native.x", "--ramflow-pid", "123"), self.backend)
        verification.check("start")
        self.backend.settings_reply = PermissionError("temporary denial")
        verification.check("during")
        self.backend.settings_reply = (123, {"DryRun": True, "Paused": False})
        verification.check("end")
        evidence = verification.export()
        self.assertEqual(evidence["label"], "USER DECLARED / unverified")
        self.assertEqual(evidence["first_unverified"]["reason"], "temporary denial")
        self.assertEqual(evidence["last"]["phase"], "end")

    def test_automatic_wait_sleeps_to_next_checkpoint_manual_is_responsive(self):
        with patch.object(observer.time, "sleep") as sleep:
            observer.Console(True).wait(8)
        sleep.assert_called_once_with(8)

    def test_main_writes_only_when_observation_has_returned(self):
        sequence = []
        with patch.object(observer, "Windows", return_value=self.backend), \
                patch.object(observer, "observe", side_effect=lambda *a: sequence.append("stop") or {"status": "completed"}), \
                patch.object(observer, "write_report", side_effect=lambda *a: sequence.append("write")), \
                contextlib.redirect_stdout(io.StringIO()):
            code = observer.main(["--condition", "absent", "--measurement", "off", "--output", "unused", "--duration", "1"])
        self.assertEqual(code, 0)
        self.assertEqual(sequence, ["stop", "write"])
        self.backend.close.assert_called_once()


class WinApiMockTests(unittest.TestCase):
    def setUp(self):
        # Bypass Windows.__init__ entirely: safe on any platform.
        self.backend = observer.Windows.__new__(observer.Windows)
        self.backend.k = Mock()
        self.backend.ps = Mock()
        self.backend.pdh = Mock()
        self.backend.query = observer.HANDLE()
        self.backend.counter = observer.HANDLE()
        self.backend.pdh_reason = None
        self.backend.identities = {}

    def test_memory_and_process_failures_are_null(self):
        self.backend.k.GlobalMemoryStatusEx.return_value = 0
        self.backend.k.OpenProcess.return_value = None
        self.assertTrue(all(v is None for v in self.backend.memory().values()))
        self.assertIsNone(self.backend.process(123)["cpu_seconds"])
        self.assertIsNone(self.backend.process(123)["private_bytes"])

    def test_pdh_missing_bad_status_and_valid_zero(self):
        self.backend.pdh.PdhOpenQueryW.return_value = -1
        self.assertIsNone(self.backend.pages_input())
        self.assertIn("unavailable", self.backend.pdh_reason)
        self.backend.pdh_reason = None
        self.backend.query = observer.HANDLE(1)
        self.backend.pdh.PdhCollectQueryData.return_value = 0

        def read(_counter, _format, _type, value):
            value._obj.status = 0xc0000bc6
            value._obj.value.double = 0
            return 0

        self.backend.pdh.PdhGetFormattedCounterValue.side_effect = read
        self.assertIsNone(self.backend.pages_input())

        def valid(_counter, _format, _type, value):
            value._obj.status = 1
            value._obj.value.double = 0
            return 0

        self.backend.pdh.PdhGetFormattedCounterValue.side_effect = valid
        self.assertEqual(self.backend.pages_input(), 0)

    def test_process_reuse_rejected_before_memory_collection(self):
        self.backend.k.OpenProcess.return_value = 1
        self.backend.identities[123] = 10
        self.backend._times = Mock(return_value=(20, 0, 5.0))
        result = self.backend.process(123)
        self.assertEqual(result["status"], "pid_reused")
        self.assertIsNone(result["cpu_seconds"])
        self.backend.ps.GetProcessMemoryInfo.assert_not_called()
        self.backend.k.CloseHandle.assert_called_once_with(1)

    def test_documented_structure_sizes(self):
        self.assertEqual(C.sizeof(observer.FileTime), 8)
        self.assertEqual(C.sizeof(observer.MemoryStatus), 64)
        self.assertEqual(C.sizeof(observer.PdhValue), 16)
        self.assertEqual(C.sizeof(observer.ProcessMemory), 80 if C.sizeof(C.c_void_p) == 8 else 44)

    def test_os_exit_timestamp_must_be_before_stop_cutoff(self):
        self.backend._times = Mock(return_value=(10000000, 30000000, 0.25))
        self.assertEqual(self.backend.workload_completion(1, 29999999), (None, False))
        self.assertEqual(self.backend.workload_completion(1, 30000000), (2.0, True))
        self.backend._times.return_value = None
        self.assertEqual(self.backend.workload_completion(1, 30000000), (None, None))

    def test_pipe_timeout_has_no_pending_io_or_unbounded_wait(self):
        self.backend.k.ReadFile.return_value = 0
        with patch.object(observer.C, "get_last_error", return_value=232, create=True), \
                patch.object(observer.time, "monotonic", side_effect=[0, 0.1, 1.1]), \
                patch.object(observer.time, "sleep"), self.assertRaisesRegex(OSError, "deadline"):
            self.backend._pipe_io(1, C.create_string_buffer(64), True, 1)
        self.backend.k.GetOverlappedResult.assert_not_called()
        self.assertIsNone(self.backend.k.ReadFile.call_args.args[-1])

    def test_settings_request_is_read_only_and_response_bounded(self):
        self.backend.k.CreateFileW.return_value = 1

        def server(_handle, pid):
            pid._obj.value = 123
            return 1

        self.backend.k.GetNamedPipeServerProcessId.side_effect = server
        self.backend.k.SetNamedPipeHandleState.return_value = 1
        payload = b'{"Ok":true,"Data":{"DryRun":true,"Paused":false}}'

        def exchange(_handle, data, reading, _deadline):
            if reading:
                self.assertEqual(C.sizeof(data), observer.MAX_PIPE_BYTES)
                data.value = payload
                return len(payload)
            self.assertEqual(data.value, b'{"Command":"settings"}')
            return len(data.value)

        self.backend._pipe_io = Mock(side_effect=exchange)
        pid, settings = self.backend.settings("RamFlow.Native.x")
        self.assertEqual(pid, 123)
        self.assertTrue(settings["DryRun"])
        self.backend.k.CloseHandle.assert_called_once_with(1)


if __name__ == "__main__":
    unittest.main()
