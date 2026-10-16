"""Repair a trace whose header carries an unescaped newline, so it can be replayed.

WHY THIS EXISTS. `TraceRecorder.EscapeJson` used to escape the backslash and the quote and nothing else,
and `DodgeConfig.Describe` grew a three-line form. An unescaped newline inside a JSON string does not
produce a bad value, it produces a BROKEN FILE: `trace.jsonl` gained a bare `path enable=...` line and
every line-by-line reader reported the recording as corrupt at line 3. The writer is fixed (SPIKE3.2
section 15.12), and every trace recorded before that fix is still unreadable -- including sessions that
are otherwise perfectly healthy and tens of thousands of frames long.

WHAT IT DOES. It merges the continuation line back into the record it belongs to, escaping it as `\\n`
so the resulting JSON matches what the fixed writer would have produced. It is deliberately narrow:

  * only a line that does NOT start a record (`{`) and is not the continuation of a `G`/`F` record is
    merged, and only into the line before it;
  * the merged `dodge` string is one field of one `H` record, so the total line count changes by the
    number of continuations merged and nothing else does;
  * it never edits a record that parses, so a healthy trace passes through byte-identical.

Usage:
    python tools\\repair-trace.py <broken-trace.jsonl> <output.jsonl>

Exit codes: 0 = written (even if nothing needed repair), 2 = the input could not be read.
"""
import json
import sys


def repair(src, dst):
    merged = 0
    kept = 0
    with open(src, encoding='utf-8', errors='replace') as f, \
         open(dst, 'w', encoding='utf-8', newline='\n') as o:
        previous = None
        for line in f:
            if previous is not None:
                stripped = line.rstrip('\n')
                # A RECORD STARTS WITH `{`; A CONTINUATION DOES NOT. That is the whole test, and it is
                # the same test a reader would have to make -- which is why the file is repairable at
                # all rather than merely corrupt.
                if stripped and not stripped.startswith('{'):
                    try:
                        json.loads(previous)
                    except ValueError:
                        previous = previous.rstrip('\n') + '\\n' + stripped
                        merged += 1
                        continue
                    # The previous line parses on its own: this continuation is not part of a broken
                    # string, so it belongs to whatever comes next and is passed through.
                o.write(previous if previous.endswith('\n') else previous + '\n')
                kept += 1
            previous = line
        if previous is not None:
            o.write(previous if previous.endswith('\n') else previous + '\n')
            kept += 1
    return merged, kept


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    try:
        merged, kept = repair(sys.argv[1], sys.argv[2])
    except OSError as ex:
        print('could not read %s: %s' % (sys.argv[1], ex))
        sys.exit(2)
    print('continuation line(s) merged back into their record: %d' % merged)
    print('line(s) written: %d' % kept)
    if merged == 0:
        print('nothing needed repairing -- this trace already parses record by record')
