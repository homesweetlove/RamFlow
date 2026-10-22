#!/usr/bin/env python3
"""Standalone Windows observer. Standard library only; never imports RamFlow.

Only the observer's priority is changed. The only IPC command is `settings`.
See docs/BENCHMARK.md for units, verification scope and experiment protocol.
"""
from __future__ import annotations

import argparse
import csv
import ctypes as C
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
from datetime import datetime, timezone

MAX_PIDS = 64
MAX_SCAN = 100000
MAX_PIPE_BYTES = 65536
BACKOFF_SECONDS = 10.0
DWORD, BOOL, HANDLE = C.c_uint32, C.c_int32, C.c_void_p
PTR = C.c_size_t


class FileTime(C.Structure):
    _fields_ = [("low", DWORD), ("high", DWORD)]

    def ticks(self):
        return (self.high << 32) | self.low


class MemoryStatus(C.Structure):
    _fields_ = [("length", DWORD), ("load", DWORD)] + [
        (name, C.c_uint64) for name in (
            "total_phys", "avail_phys", "total_pagefile", "avail_pagefile",
            "total_virtual", "avail_virtual", "avail_extended_virtual")]


class ProcessMemory(C.Structure):
    _fields_ = [("cb", DWORD), ("page_faults", DWORD)] + [
        (name, PTR) for name in ("peak_ws", "ws", "peak_paged", "paged",
                                "peak_nonpaged", "nonpaged", "pagefile",
                                "peak_pagefile", "private")]


class ProcessEntry(C.Structure):
    _fields_ = [("size", DWORD), ("usage", DWORD), ("pid", DWORD),
                ("heap", PTR), ("module", DWORD), ("threads", DWORD),
                ("parent", DWORD), ("priority", C.c_int32), ("flags", DWORD),
                ("exe", C.c_wchar * 260)]


class PdhUnion(C.Union):
    _fields_ = [("double", C.c_double), ("large", C.c_int64), ("long", C.c_int32)]


class PdhValue(C.Structure):
    _fields_ = [("status", DWORD), ("value", PdhUnion)]


class Rejected(RuntimeError):
    pass


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


class Aggregate:
    """Constant space; sample-weighted moments, no retained sample history."""
    def __init__(self):
        self.count = self.missing = 0
        self.mean = self.m2 = 0.0
        self.minimum = self.maximum = self.first = self.last = None

    def add(self, value):
        if not finite(value):
            self.missing += 1
            return
        self.count += 1
        delta = value - self.mean
        self.mean += delta / self.count
        self.m2 += delta * (value - self.mean)
        self.minimum = value if self.minimum is None else min(self.minimum, value)
        self.maximum = value if self.maximum is None else max(self.maximum, value)
        if self.first is None:
            self.first = value
        self.last = value

    def export(self):
        return dict(count=self.count, missing=self.missing,
                    mean=self.mean if self.count else None,
                    sample_stdev=math.sqrt(max(0, self.m2 / (self.count - 1))) if self.count > 1 else None,
                    min=self.minimum, max=self.maximum, first=self.first, last=self.last)


class Windows:
    """Documented Win32 APIs, with explicit pointer-sized ABI declarations."""
    def __init__(self):
        if os.name != "nt":
            raise OSError("Live observation requires Windows; mocked unittests are portable.")
        self.k = C.WinDLL("kernel32", use_last_error=True)
        self.ps = C.WinDLL("psapi", use_last_error=True)
        self.pdh = C.WinDLL("pdh", use_last_error=True)
        self.query = HANDLE()
        self.counter = HANDLE()
        self.pdh_reason = None
        self.identities = {}
        self._bind()

    @staticmethod
    def _signature(dll, name, result, *args):
        fn = getattr(dll, name)
        fn.restype, fn.argtypes = result, list(args)

    def _bind(self):
        p = C.POINTER
        specs = [
            ("GetCurrentProcess", HANDLE, ()),
            ("SetPriorityClass", BOOL, (HANDLE, DWORD)),
            ("OpenProcess", HANDLE, (DWORD, BOOL, DWORD)),
            ("CloseHandle", BOOL, (HANDLE,)),
            ("GetSystemTimes", BOOL, (p(FileTime), p(FileTime), p(FileTime))),
            ("GlobalMemoryStatusEx", BOOL, (p(MemoryStatus),)),
            ("GetProcessTimes", BOOL, (HANDLE, p(FileTime), p(FileTime), p(FileTime), p(FileTime))),
            ("CreateToolhelp32Snapshot", HANDLE, (DWORD, DWORD)),
            ("Process32FirstW", BOOL, (HANDLE, p(ProcessEntry))),
            ("Process32NextW", BOOL, (HANDLE, p(ProcessEntry))),
            ("CreateFileW", HANDLE, (C.c_wchar_p, DWORD, DWORD, HANDLE, DWORD, DWORD, HANDLE)),
            ("GetNamedPipeServerProcessId", BOOL, (HANDLE, p(DWORD))),
            ("SetNamedPipeHandleState", BOOL, (HANDLE, p(DWORD), p(DWORD), p(DWORD))),
            ("WriteFile", BOOL, (HANDLE, C.c_void_p, DWORD, p(DWORD), C.c_void_p)),
            ("ReadFile", BOOL, (HANDLE, C.c_void_p, DWORD, p(DWORD), C.c_void_p)),
        ]
        for name, result, args in specs:
            self._signature(self.k, name, result, *args)
        self._signature(self.ps, "GetProcessMemoryInfo", BOOL, HANDLE, p(ProcessMemory), DWORD)
        for name, args in [
            ("PdhOpenQueryW", (C.c_wchar_p, PTR, p(HANDLE))),
            ("PdhAddEnglishCounterW", (HANDLE, C.c_wchar_p, PTR, p(HANDLE))),
            ("PdhCollectQueryData", (HANDLE,)),
            ("PdhGetFormattedCounterValue", (HANDLE, DWORD, p(DWORD), p(PdhValue))),
            ("PdhCloseQuery", (HANDLE,)),
        ]:
            self._signature(self.pdh, name, C.c_int32, *args)

    def lower_priority(self):
        return bool(self.k.SetPriorityClass(self.k.GetCurrentProcess(), 0x4000))  # BELOW_NORMAL

    def system_times(self):
        idle, kernel, user = FileTime(), FileTime(), FileTime()
        if not self.k.GetSystemTimes(C.byref(idle), C.byref(kernel), C.byref(user)):
            return None
        return idle.ticks(), kernel.ticks(), user.ticks()

    def memory(self):
        m = MemoryStatus()
        m.length = C.sizeof(m)
        if not self.k.GlobalMemoryStatusEx(C.byref(m)):
            return {k: None for k in ("memory_load_percent", "physical_total_bytes",
                    "physical_available_bytes", "commit_limit_process_bytes", "commit_used_estimate_bytes")}
        return dict(memory_load_percent=m.load, physical_total_bytes=m.total_phys,
                    physical_available_bytes=m.avail_phys, commit_limit_process_bytes=m.total_pagefile,
                    commit_used_estimate_bytes=m.total_pagefile - m.avail_pagefile)

    def init_pages(self):
        if self.query.value or self.pdh_reason:
            return
        status = self.pdh.PdhOpenQueryW(None, 0, C.byref(self.query))
        if not status:
            status = self.pdh.PdhAddEnglishCounterW(
                self.query, r"\Memory\Pages Input/sec", 0, C.byref(self.counter))
        if status:
            self.pdh_reason = "PDH unavailable: 0x%08x" % (status & 0xffffffff)

    def pages_input(self):
        self.init_pages()
        if self.pdh_reason:
            return None
        value = PdhValue()
        if self.pdh.PdhCollectQueryData(self.query):
            return None
        if self.pdh.PdhGetFormattedCounterValue(self.counter, 0x200, None, C.byref(value)):
            return None
        # PDH_CSTATUS_VALID_DATA and PDH_CSTATUS_NEW_DATA only.
        return value.value.double if value.status in (0, 1) and finite(value.value.double) and value.value.double >= 0 else None

    def _times(self, handle):
        created, exited, kernel, user = FileTime(), FileTime(), FileTime(), FileTime()
        if not self.k.GetProcessTimes(handle, C.byref(created), C.byref(exited),
                                      C.byref(kernel), C.byref(user)):
            return None
        return created.ticks(), exited.ticks(), (kernel.ticks() + user.ticks()) / 1e7

    def process(self, pid):
        result = dict(cpu_seconds=None, working_set_bytes=None, private_bytes=None,
                      identity=None, status="unavailable")
        handle = self.k.OpenProcess(0x1000, False, pid)  # QUERY_LIMITED_INFORMATION
        if not handle:
            return result
        try:
            times = self._times(handle)
            if times:
                identity, exited, cpu = times
                first = self.identities.setdefault(pid, identity)
                if identity != first:
                    result["status"] = "pid_reused"
                    return result
                if exited:
                    result["status"] = "exited"
                    return result
                result.update(identity=identity, cpu_seconds=cpu, status="ok")
            # Memory may still be available when process times are denied.
            m = ProcessMemory()
            m.cb = C.sizeof(m)
            if self.ps.GetProcessMemoryInfo(handle, C.byref(m), m.cb):
                result.update(working_set_bytes=m.ws, private_bytes=m.private)
            return result
        finally:
            self.k.CloseHandle(handle)

    def ramflow_processes(self):
        handle = self.k.CreateToolhelp32Snapshot(2, 0)  # TH32CS_SNAPPROCESS
        if handle in (None, C.c_void_p(-1).value):
            return None
        found = []  # bounded by MAX_PIDS; scanning never retains the process inventory
        try:
            entry = ProcessEntry()
            entry.size = C.sizeof(entry)
            ok = self.k.Process32FirstW(handle, C.byref(entry))
            scanned = 0
            while ok:
                scanned += 1
                if scanned > MAX_SCAN:
                    return None
                name = entry.exe.casefold()
                if name in ("ramflow", "ramflow.exe", "ramflow.service", "ramflow.service.exe"):
                    if len(found) == MAX_PIDS:
                        return None
                    found.append(dict(pid=entry.pid, name=entry.exe))
                ok = self.k.Process32NextW(handle, C.byref(entry))
            return found if C.get_last_error() == 18 else None  # ERROR_NO_MORE_FILES
        finally:
            self.k.CloseHandle(handle)

    def _pipe_io(self, handle, data, reading, deadline):
        # PIPE_NOWAIT gives immediate results. A bounded poll avoids pending native
        # buffers and an unbounded cancellation drain at an overlapped timeout.
        while time.monotonic() < deadline:
            transferred = DWORD()
            fn = self.k.ReadFile if reading else self.k.WriteFile
            ok = fn(handle, data, C.sizeof(data) if reading else len(data.value),
                    C.byref(transferred), None)
            error = 0 if ok else C.get_last_error()
            if reading and error == 232:  # ERROR_NO_DATA: no message available yet
                time.sleep(min(0.01, max(0, deadline - time.monotonic())))
                continue
            if error:
                raise OSError("settings pipe I/O error %s (including oversized response)" % error)
            if transferred.value == 0:
                raise OSError("settings pipe closed")
            return transferred.value
        raise OSError("settings pipe deadline exceeded")

    def settings(self, name):
        # Local, explicit RamFlow pipes only; no automatic discovery or configuration writes.
        handle = self.k.CreateFileW("\\\\.\\pipe\\" + name, 0x120183, 0, None,
                                     3, 0x00110000, None)  # identification SQOS; no overlapped I/O
        if handle in (None, C.c_void_p(-1).value):
            raise OSError("settings pipe unavailable (error %s)" % C.get_last_error())
        try:
            server = DWORD()
            if not self.k.GetNamedPipeServerProcessId(handle, C.byref(server)):
                raise OSError("settings pipe server PID unavailable")
            mode = DWORD(2 | 1)  # PIPE_READMODE_MESSAGE | PIPE_NOWAIT (client handle only)
            if not self.k.SetNamedPipeHandleState(handle, C.byref(mode), None, None):
                raise OSError("settings pipe message mode unavailable")
            deadline = time.monotonic() + 1.0
            request = C.create_string_buffer(b'{"Command":"settings"}')
            if self._pipe_io(handle, request, False, deadline) != len(request.value):
                raise OSError("short settings request")
            response = C.create_string_buffer(MAX_PIPE_BYTES)
            size = self._pipe_io(handle, response, True, deadline)
            message = json.loads(response.raw[:size])
            if message.get("Ok") is not True or not isinstance(message.get("Data"), dict):
                raise ValueError("settings pipe response not successful")
            return server.value, message["Data"]
        finally:
            self.k.CloseHandle(handle)

    def workload_handle(self, process):
        return self.k.OpenProcess(0x1000, False, process.pid)

    def workload_completion(self, handle, stop_filetime):
        times = self._times(handle) if handle else None
        if not times or not times[1] or times[1] < times[0]:
            return None, None
        by_stop = times[1] <= stop_filetime
        return ((times[1] - times[0]) / 1e7 if by_stop else None), by_stop

    def close_workload(self, handle):
        if handle:
            self.k.CloseHandle(handle)

    def close(self):
        if self.query.value:
            self.pdh.PdhCloseQuery(self.query)
            self.query = HANDLE()


class Verification:
    """Retain counts and first/latest evidence only, not growing checkpoint lists."""
    def __init__(self, args, backend):
        self.args, self.backend = args, backend
        self.count = self.verified = self.unverified = 0
        self.first = self.last = None
        self.rejection = None
        self.first_unverified = self.last_unverified = None

    def check(self, phase):
        evidence = dict(phase=phase, condition=self.args.condition,
                        label="USER DECLARED / unverified", server_pid=None)
        problem = None
        if self.args.condition == "absent":
            found = self.backend.ramflow_processes()
            evidence["detected"] = found
            if found is None:
                problem = "Cannot establish absent condition: process enumeration unavailable"
            elif found:
                problem = "Absent condition rejected: RamFlow/RamFlow.Service detected"
            else:
                evidence["label"] = "VERIFIED at process-name checkpoint only"
        elif self.args.settings_pipe:
            try:
                pid, settings = self.backend.settings(self.args.settings_pipe)
                evidence.update(server_pid=pid, dry_run=settings.get("DryRun"),
                                paused=settings.get("Paused"))
                valid_fields = type(settings.get("DryRun")) is bool and type(settings.get("Paused")) is bool
                if valid_fields and (settings["DryRun"] != (self.args.condition == "dry-run") or settings["Paused"]):
                    problem = "Declared condition contradicts pipe DryRun/Paused settings"
                elif self.args.ramflow_pid and pid not in self.args.ramflow_pid:
                    problem = "Pipe server PID contradicts explicit --ramflow-pid list"
                elif pid not in self.args.ramflow_pid:
                    evidence["reason"] = "Pipe server PID not in explicit --ramflow-pid list"
                elif not valid_fields:
                    evidence["reason"] = "DryRun/Paused boolean fields missing"
                else:
                    evidence["label"] = "VERIFIED named pipe server settings at checkpoint only"
            except (OSError, ValueError, TypeError, AttributeError) as exc:
                evidence["reason"] = str(exc)[:240]
        else:
            evidence["reason"] = "No --settings-pipe supplied"
        if problem:
            evidence.update(label="REJECTED", reason=problem)
            self.rejection = self.rejection or problem
        self.count += 1
        if evidence["label"].startswith("VERIFIED"):
            self.verified += 1
        else:
            self.unverified += 1
            self.first_unverified = self.first_unverified or evidence
            self.last_unverified = evidence
        self.first = self.first or evidence
        self.last = evidence
        if problem:
            raise Rejected(problem)

    def export(self):
        label = "REJECTED" if self.rejection else (
            "VERIFIED at sampled checkpoints only" if not self.unverified else "USER DECLARED / unverified")
        return dict(label=label, checkpoints=self.count, verified_checkpoints=self.verified,
                    unverified_checkpoints=self.unverified, first=self.first, last=self.last,
                    first_unverified=self.first_unverified, last_unverified=self.last_unverified,
                    rejection=self.rejection,
                    scope="Named server settings or executable names only; gaps and other engines are not verified")


class Sampler:
    def __init__(self, backend, pids, clock=time.monotonic):
        self.backend, self.pids, self.clock = backend, pids, clock
        self.previous_system = None
        self.previous_process = {}
        self.aggregates = {}
        self.first = self.last = None
        self.samples = 0
        self.gaps = Aggregate()
        self.process_status = {}

    def sample(self):
        at = self.clock()
        if self.last:
            self.gaps.add(max(0, at - self.last["monotonic_seconds"]))
        values = self.backend.memory()
        times = self.backend.system_times()
        cpu = None
        if times is not None and self.previous_system is not None:
            idle = times[0] - self.previous_system[0]
            total = times[1] + times[2] - self.previous_system[1] - self.previous_system[2]
            if total > 0 and 0 <= idle <= total:
                cpu = 100 * (total - idle) / total
        self.previous_system = times
        values.update(system_cpu_percent=cpu, pages_input_per_second=self.backend.pages_input())
        for role, pid in self.pids:
            process = self.backend.process(pid)
            self.process_status[role] = dict(pid=pid, identity=process.get("identity"),
                                             status=process.get("status", "unavailable"))
            prefix = role + "."
            for key in ("cpu_seconds", "working_set_bytes", "private_bytes"):
                values[prefix + key] = process.get(key)
            prior = self.previous_process.get(pid)
            seconds = process.get("cpu_seconds")
            identity = process.get("identity")
            percent = None
            if prior and finite(seconds) and identity is not None and identity == prior[2] and at > prior[0]:
                delta = seconds - prior[1]
                if delta >= 0:
                    percent = 100 * delta / (at - prior[0])  # one logical CPU = 100%
            self.previous_process[pid] = (at, seconds, identity) if finite(seconds) else None
            values[prefix + "cpu_percent_one_core"] = percent
        for key, value in values.items():
            self.aggregates.setdefault(key, Aggregate()).add(value)
        self.samples += 1
        snapshot = dict(monotonic_seconds=at, metrics=values)
        self.first = self.first or snapshot
        self.last = snapshot
        return values


class Console:
    def __init__(self, automatic):
        self.automatic = automatic

    def start(self):
        if not self.automatic:
            if not sys.stdin.isatty():
                raise ValueError("Manual mode needs an interactive console; use --duration > 0 for automation")
            input("Press Enter to START observation. ")
            print("Observing. Press Enter to STOP (workload will be left running).", flush=True)

    def wait(self, seconds):
        if not self.automatic:
            import msvcrt
            while msvcrt.kbhit():
                if msvcrt.getwch() in ("\r", "\n"):
                    return True
        time.sleep(max(0, seconds if self.automatic else min(seconds, 0.25)))
        return False


def interval_for(values, elapsed, base):
    high = (finite(values.get("system_cpu_percent")) and values["system_cpu_percent"] >= 85) or (
        finite(values.get("memory_load_percent")) and values["memory_load_percent"] >= 90)
    return max(base, BACKOFF_SECONDS, elapsed * 10) if high or elapsed >= base * 0.25 else base


def launch_workload(command):
    # Explicit NORMAL prevents below-normal observer priority inheritance; a new group
    # prevents observer Ctrl+C from being delivered to this child. No shell or pipes.
    return subprocess.Popen(command, shell=False, stdin=subprocess.DEVNULL,
                            creationflags=0x20 | 0x200)


def observe(args, backend, control, clock=time.monotonic, launcher=launch_workload):
    verification = Verification(args, backend)
    verification.check("startup")  # rejection before start creates no output
    control.start()
    verification.check("start")
    priority = backend.lower_priority()
    sampler = Sampler(backend, [("observer", os.getpid())] + [
        ("ramflow_%d" % pid, pid) for pid in args.ramflow_pid], clock)
    overhead = Aggregate()
    workload = handle = None
    report = dict(schema_version=1, started_utc=datetime.now(timezone.utc).isoformat(),
                  condition=args.condition,
                  measurement=dict(requested=args.measurement, label="USER DECLARED / unverified",
                                   scope="Built-in benchmark state; observer never changes it"),
                  observer=dict(pid=os.getpid(), below_normal_priority_applied=priority,
                                endpoint_only=args.duration == 0, sample_seconds=args.sample_seconds),
                  ramflow_pids=args.ramflow_pid, status="completed", stop_reason=None)
    started = clock()
    backoffs = 0
    next_sample = started
    try:
        if args.workload:
            workload = launcher(args.workload)
            handle = backend.workload_handle(workload)
        while True:
            before = clock()
            if next_sample is not None and before >= next_sample:
                verification.check("during")
                values = sampler.sample()
                cost = max(0, clock() - before)
                overhead.add(cost)
                interval = interval_for(values, cost, args.sample_seconds)
                backoffs += int(interval > args.sample_seconds)
                next_sample = None if args.duration == 0 else clock() + interval
            now = clock()
            if args.duration is not None and args.duration > 0 and now - started >= args.duration:
                report["stop_reason"] = "duration"
                break
            wait = max(0, next_sample - now) if next_sample is not None else 0.25
            if args.duration and args.duration > 0:
                wait = min(wait, max(0, args.duration - (now - started)))
            if control.wait(wait):
                report["stop_reason"] = "enter"
                break
    except KeyboardInterrupt:
        report["stop_reason"] = "interrupt"
    except (Rejected, OSError, ValueError) as exc:
        report.update(status="rejected" if isinstance(exc, Rejected) else "error",
                      stop_reason=str(exc)[:240])
    finally:
        # Stop timestamp precedes final checks, endpoint read and export.
        stopped = clock()
        stop_filetime = time.time_ns() // 100 + 116444736000000000  # UTC FILETIME epoch
        report["elapsed_seconds"] = max(0, stopped - started)
        # Capture workload state at STOP, before a potentially slow final IPC read.
        # A workload completing during finalization must remain unfinished for this run.
        exit_code = workload.poll() if workload is not None else None
        completion, by_stop = (backend.workload_completion(handle, stop_filetime)
                               if workload is not None and exit_code is not None else (None, False))
        try:
            before = clock()
            # Read counters before potentially slow end-verification IPC. The
            # endpoint timestamp/span remain explicit, separate from STOP elapsed.
            sampler.sample()
        except (OSError, ValueError) as exc:
            report.update(status="error", stop_reason=str(exc)[:240])
        report["observer"]["final_counter_read_wall_seconds"] = max(0, clock() - before)
        try:
            verification.check("end")
        except Rejected as exc:
            report.update(status="rejected", stop_reason=str(exc))
        overhead.add(max(0, clock() - before))
        report["workload"] = dict(command=args.workload or None,
                                  pid=workload.pid if workload is not None else None,
                                  exit_code=exit_code if by_stop is True else None,
                                  still_running=(not by_stop if by_stop is not None else None) if workload else None,
                                  completed_by_stop=by_stop if workload else None,
                                  completion_duration_seconds=completion,
                                  duration_source="GetProcessTimes exit minus creation; null if unavailable or unfinished",
                                  never_terminated=True)
        backend.close_workload(handle)
    report["condition_evidence"] = verification.export()
    report["observer"].update(samples=sampler.samples, load_backoffs=backoffs,
                              sampling_wall_seconds=overhead.export(),
                              sampling_wall_total_seconds=overhead.mean * overhead.count,
                              sample_gaps_seconds=sampler.gaps.export(),
                              latest_process_status=sampler.process_status,
                              pdh_unavailable_reason=getattr(backend, "pdh_reason", None))
    first = sampler.first["metrics"] if sampler.first else {}
    last = sampler.last["metrics"] if sampler.last else {}
    cpu_start, cpu_end = first.get("observer.cpu_seconds"), last.get("observer.cpu_seconds")
    report["observer"]["endpoint_cpu_delta_seconds"] = (
        cpu_end - cpu_start if finite(cpu_start) and finite(cpu_end) and cpu_end >= cpu_start else None)
    report["observer"]["endpoint_span_seconds"] = (
        sampler.last["monotonic_seconds"] - sampler.first["monotonic_seconds"] if sampler.first else None)
    report["observer"]["last_endpoint_started_seconds_after_stop"] = (
        sampler.last["monotonic_seconds"] - stopped if sampler.last else None)
    report["metrics"] = {key: value.export() for key, value in sampler.aggregates.items()}
    report["endpoints"] = dict(first=sampler.first, last=sampler.last)
    report["stopped_utc"] = datetime.now(timezone.utc).isoformat()
    return report


def write_report(prefix, report):
    """Called only after observe returns. CSV size is bounded by PID/metric limits."""
    path = Path(prefix)
    path.parent.mkdir(parents=True, exist_ok=True)
    with Path(str(path) + ".json").open("w", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, allow_nan=False)
        stream.write("\n")
    with Path(str(path) + ".csv").open("w", encoding="utf-8", newline="") as stream:
        columns = ["condition", "condition_label", "measurement", "measurement_label", "metric",
                   "count", "missing", "mean", "sample_stdev", "min", "max", "first", "last"]
        writer = csv.DictWriter(stream, fieldnames=columns)
        writer.writeheader()
        for metric, stats in report["metrics"].items():
            writer.writerow(dict(condition=report["condition"],
                                 condition_label=report["condition_evidence"]["label"],
                                 measurement=report["measurement"]["requested"],
                                 measurement_label=report["measurement"]["label"], metric=metric, **stats))


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--condition", required=True, choices=("absent", "dry-run", "intervention"))
    parser.add_argument("--measurement", required=True, choices=("off", "on"),
                        help="Externally selected built-in benchmark label; never switches RamFlow settings")
    parser.add_argument("--output", required=True, help="Output prefix (.json and .csv appended after stop)")
    parser.add_argument("--ramflow-pid", action="append", nargs="+", type=int, default=[])
    parser.add_argument("--settings-pipe", help="Explicit local RamFlow.Native.* pipe name, without \\\\.\\pipe\\")
    parser.add_argument("--acknowledge-existing-intervention", action="store_true")
    parser.add_argument("--sample-seconds", type=float, default=2.0)
    parser.add_argument("--duration", type=float, help="Positive seconds: automatic; 0: manual endpoint-only")
    parser.add_argument("--workload", nargs=argparse.REMAINDER, default=[])
    args = parser.parse_args(argv)
    args.ramflow_pid = list(dict.fromkeys(pid for group in args.ramflow_pid for pid in group))
    if len(args.ramflow_pid) > MAX_PIDS or any(pid <= 0 or pid > 0xffffffff for pid in args.ramflow_pid):
        parser.error("--ramflow-pid requires at most %d unique positive DWORD PIDs" % MAX_PIDS)
    if not finite(args.sample_seconds) or args.sample_seconds < 2:
        parser.error("--sample-seconds must be finite and >= 2")
    if args.duration is not None and (not finite(args.duration) or args.duration < 0):
        parser.error("--duration must be finite and >= 0")
    if args.condition == "intervention" and not args.acknowledge_existing_intervention:
        parser.error("intervention requires --acknowledge-existing-intervention; only observes current mode")
    if args.condition == "absent" and (args.ramflow_pid or args.settings_pipe):
        parser.error("absent cannot have RamFlow PIDs or a settings pipe")
    if args.settings_pipe and (not args.settings_pipe.startswith("RamFlow.Native.") or
                              len(args.settings_pipe) > 240 or any(c in args.settings_pipe for c in "\\/\x00")):
        parser.error("--settings-pipe must be a local RamFlow.Native.* name without path separators")
    if "--workload" in (argv if argv is not None else sys.argv[1:]) and not args.workload:
        parser.error("--workload requires an executable (put workload and its arguments last)")
    return args


def main(argv=None):
    args = parse_args(argv)
    if args.condition == "intervention":
        print("WARNING: only observes the EXISTING intervention mode; does not activate or change it.", file=sys.stderr)
    backend = None
    try:
        backend = Windows()
        report = observe(args, backend, Console(args.duration is not None and args.duration > 0))
        write_report(args.output, report)
        print("Stopped; wrote %s.json and %s.csv (%s)." % (args.output, args.output, report["status"]))
        return 0 if report["status"] == "completed" else 2
    except (OSError, ValueError, Rejected, EOFError) as exc:
        print("Observer refused/failed: %s" % exc, file=sys.stderr)
        return 2
    finally:
        if backend is not None:
            backend.close()


if __name__ == "__main__":
    sys.exit(main())
