# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
"""Takes every thread's backtrace of a hung app on a connected phone, twice, without running code in it.

Run by the device launcher (build/Models/HungProcessStacks.cs, HungProcessSampler.DeviceSession)
when a launch budget runs out with the app still running:

    xcrun python3 -I build/scripts/device-hung-stacks.py <device-uuid> <pid> <samples> <gap-seconds> <attach-seconds> [<symbol-file>...]

It drives lldb's CoreDevice support through the SB API rather than `lldb --batch`:
`device process attach` returns before the attach has stopped the process, and a batch session
runs `thread backtrace all` at once, which refuses a running process and ends the batch before
its `process detach`. Here the session waits (bounded) for the stop, takes the first backtrace,
lets the app run for the gap, stops it again for the second, and detaches, so the app is left
exactly as it was found. Nothing is evaluated in the app: no expressions, no frame variables, and
the thread and frame formats name functions without their arguments, since formatting an argument
value can make lldb call into the app (its Objective-C runtime support reads class data that way).

The attach reads every loaded image's headers from the phone's memory when this Mac has no copy
of the phone's shared cache, which takes minutes over a network link at lldb's default
module-load level. `target.memory-module-load-level minimal` keeps that to sections and UUIDs;
frames are still attributed to their images, and `image list -o -f` gives each image's load
address. A packaged app is stripped of local symbols, so each <symbol-file> (the build's unstripped
copy of an app image) is added once the app has stopped; lldb matches it to its image by UUID and
names that image's frames. A file no loaded image matches is reported and ignored.

Output echoes each command as `(lldb) <command>` and marks each sample with a
`=== hung-stacks sample N start=<s> end=<s> ===` line (seconds since the script started), which
the launcher splits into one file per sample. The exit code is 0 when every sample has a
backtrace, 2 when the attach never stopped the process, 3 otherwise.
"""

import re
import subprocess
import sys
import time

T0 = time.monotonic()

# lldb's defaults, less every argument value and the thread's return value, and with the raw stop
# reason: the plain one asks the frame recognizers, which may read values in the app.
THREAD_FORMAT = ("\"thread #${thread.index}{, name = '${thread.name}'}{, queue = '${thread.queue}'}"
                 "{, stop reason = ${thread.stop-reason-raw}}\\n\"")
FRAME_ROW = re.compile(r"^\s*\*?\s*frame #\d+", re.MULTILINE)
FRAME_FORMAT = ("\"frame #${frame.index}: ${frame.pc}{ ${module.file.basename}"
                "{`${function.name-without-args}{${frame.no-debug}${function.pc-offset}}}}"
                "{ at ${line.file.basename}:${line.number}}\\n\"")


def now():
    return time.monotonic() - T0


def say(text):
    print(text, flush=True)


def main(argv):
    udid, pid, samples, gap, attach_bound = argv[1], int(argv[2]), int(argv[3]), float(argv[4]), float(argv[5])
    symbol_files = argv[6:]
    lldb_path = subprocess.run(["xcrun", "lldb", "-P"], stdin=subprocess.DEVNULL, capture_output=True,
                               text=True, check=True).stdout.strip()
    sys.path.insert(0, lldb_path)
    import lldb  # noqa: E402  (lives beside the selected Xcode's lldb)

    debugger = lldb.SBDebugger.Create(False)
    debugger.SetAsync(True)
    interpreter = debugger.GetCommandInterpreter()
    listener = debugger.GetListener()

    def run(command):
        result = lldb.SBCommandReturnObject()
        interpreter.HandleCommand(command, result)
        say(f"(lldb) {command}")
        text = (result.GetOutput() or "") + (result.GetError() or "")
        if text.strip():
            say(text.rstrip())
        return result.Succeeded()

    def state_name(state):
        return lldb.SBDebugger.StateAsCString(state)

    def wait_for(process, wanted, seconds):
        """Pumps the debugger's events until the process reaches one of `wanted` or `seconds` pass."""
        deadline = now() + seconds
        event = lldb.SBEvent()
        while process.GetState() not in wanted and now() < deadline:
            if listener.WaitForEvent(1, event) and lldb.SBProcess.EventIsProcessEvent(event):
                say(f"[{now():.1f}s] process {state_name(lldb.SBProcess.GetStateFromEvent(event))}")
        return process.GetState() in wanted

    gone = (lldb.eStateExited, lldb.eStateDetached, lldb.eStateCrashed)
    captured = 0
    try:
        run("settings set target.memory-module-load-level minimal")
        run("settings set target.preload-symbols false")
        run("settings set target.prefer-dynamic-value no-dynamic-values")
        run("settings set thread-format " + THREAD_FORMAT)
        run("settings set thread-stop-format " + THREAD_FORMAT)
        run("settings set frame-format " + FRAME_FORMAT)
        # Recognizers run when a stop is described or a backtrace is taken and can evaluate in the app.
        run("frame recognizer clear")
        run(f"device select {udid}")
        run(f"device process attach --pid {pid}")
        process = debugger.GetSelectedTarget().GetProcess()
        if not process.IsValid():
            say("error: the attach created no process")
            return 2
        if not wait_for(process, (lldb.eStateStopped,) + gone, attach_bound) or process.GetState() != lldb.eStateStopped:
            say(f"error: the attach did not stop process {pid} within {attach_bound:.0f} s "
                f"(state {state_name(process.GetState())})")
            return 2
        say(f"[{now():.1f}s] attached; process {pid} stopped")
        for path in symbol_files:
            run(f'target symbols add "{path}"')

        for index in range(1, samples + 1):
            if index > 1:
                if not run("process continue") or not wait_for(process, (lldb.eStateRunning,) + gone, 30):
                    say("error: the process did not resume between samples")
                    break
                if process.GetState() in gone:
                    say(f"error: process {pid} is gone ({state_name(process.GetState())})")
                    break
                time.sleep(gap)
                if not run("process interrupt") or not wait_for(process, (lldb.eStateStopped,) + gone, 60) \
                        or process.GetState() != lldb.eStateStopped:
                    say(f"error: the process did not stop for sample {index} (state {state_name(process.GetState())})")
                    break
            # The backtrace is written before the image list runs, so a list that hangs until the
            # session is killed cannot take the backtrace with it.
            start = now()
            result = lldb.SBCommandReturnObject()
            interpreter.HandleCommand("thread backtrace all", result)
            backtrace = ((result.GetOutput() or "") + (result.GetError() or "")).rstrip()
            say(f"=== hung-stacks sample {index} start={start:.1f} end={now():.1f} ===")
            say(f"(lldb) thread backtrace all\n{backtrace}")
            if result.Succeeded() and FRAME_ROW.search(backtrace):
                captured += 1
            run("image list -o -f")
    finally:
        process = debugger.GetSelectedTarget().GetProcess()
        if process.IsValid() and process.GetState() not in gone + (lldb.eStateInvalid,):
            if process.GetState() != lldb.eStateStopped:
                # A detach needs a stopped process. An attach that never settled is left to the
                # debugger's teardown, which on CoreCLR has been seen to end the app; the capture
                # then holds no backtrace, and the stall probe fails on that.
                run("process interrupt")
                wait_for(process, (lldb.eStateStopped,) + gone, 30)
            run("process detach")
            wait_for(process, gone, 30)
            say(f"[{now():.1f}s] process {pid} {state_name(process.GetState())}")
        lldb.SBDebugger.Destroy(debugger)
    return 0 if captured == samples else 3


if __name__ == "__main__":
    sys.exit(main(sys.argv))
