"""Summarize the checked-in diagnostic run without executing or retuning it."""
import json
import pathlib
import sys
import argparse

sys.stdout.reconfigure(encoding='utf-8')

root = pathlib.Path(__file__).parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('snapshot', nargs='?', default='baseline.json', help='Snapshot name beside this script (default: baseline.json)')
args = parser.parse_args()
report = json.loads((root / args.snapshot).read_text(encoding='utf-8'))

print('| Subset | Arm | Required files | Relevant lines | Evidence-complete tasks | Oracle follow-up reads | Follow-up body tokens |')
print('| --- | --- | ---: | ---: | ---: | ---: | ---: |')
for subset in ['historical', 'holdout', 'CSharp', 'JavaScript', 'TypeScript']:
    tasks = [t for t in report['tasks'] if
             (t['historical_diagnostic'] if subset == 'historical' else
              not t['historical_diagnostic'] if subset == 'holdout' else
              not t['historical_diagnostic'] and t['language'] == subset)]
    files = sum(t['required_files'] for t in tasks)
    lines = sum(t['required_lines'] for t in tasks)
    eligible = sum(t['eligible_required_files'] for t in tasks)
    print(f'| {subset} | eligible candidates | {eligible}/{files} | — | — | — | — |')
    for name in [arm['name'] for arm in report['tasks'][0]['arms']]:
        arms = [next(a for a in t['arms'] if a['name'] == name) for t in tasks]
        delivered_files = sum(a['required_files_delivered'] for a in arms)
        delivered_lines = sum(a['relevant_lines_delivered'] for a in arms)
        complete = sum(not a['oracle_followup_reads'] for a in arms)
        reads = sum(len(a['oracle_followup_reads']) for a in arms)
        tokens = sum(r['body_tokens'] for a in arms for r in a['oracle_followup_reads'])
        print(f'| {subset} | {name} | {delivered_files}/{files} | {delivered_lines}/{lines} | {complete}/{len(tasks)} | {reads} | {tokens:,} |')

print('\nHistorical required-file diagnoses (eligible rank; top-8 rank; delivered ranks by arm):')
for task in report['tasks']:
    if task['historical_diagnostic']:
        for file in task['file_diagnostics']:
            ranks = file['delivered_ranks']
            print(task['task_id'], file['path'], file['eligible_rank'], file['unbudgeted_rank'],
                  ranks)
