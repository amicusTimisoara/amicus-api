#!/usr/bin/env python3
"""Remove exactly what scripts/seed_demo.py created — the demo „cărți”.

The seed is idempotent by NAME, so "what it created" is precisely the specialists
whose names are in seed_demo.CARTI, plus their roster rows (event_specialists),
slot patterns and slots. This deletes that set and nothing else.

Unlike the seed, this is DB-side, not HTTP: the API has no delete-specialist
endpoint, and the demo „cărți” have no login of their own to withdraw through. So
it runs where the database is — on the Pi, against the amicus-postgres container.

    python scripts/unseed_demo.py --env stage            # DRY RUN: show what would go
    python scripts/unseed_demo.py --env stage --apply    # actually remove
    python scripts/unseed_demo.py --env prod --apply --yes-this-is-production

Safety:
  * Dry run by default — nothing is deleted without --apply.
  * Prod (--env prod) additionally requires --yes-this-is-production.
  * If any demo slot has a booking, it refuses unless --force-booked, because a
    booking is a real student expecting a meeting. Deleting the slot deletes the
    booking (FK cascade), so that must be a deliberate choice.
  * Deletes a `demo-`-slugged event only if it is left empty afterwards — never a
    real event the seed reused.
"""

import argparse
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from seed_demo import CARTI  # noqa: E402  — the single source of truth for the names

ENV_FILES = {
    "stage": "~/.config/amicus/api-stage.env",
    "prod": "~/.config/amicus/api.env",
}
CONTAINER = "amicus-postgres"  # Pi-local Postgres; both amicus_stage and amicus_prod live here.

NAMES = [row[0] for row in CARTI]


def _conn(env_file: str) -> dict:
    text = open(os.path.expanduser(env_file)).read()
    m = re.search(r"ConnectionStrings__Postgres=(.+)", text)
    if not m:
        sys.exit(f"No ConnectionStrings__Postgres in {env_file}")
    cs = m.group(1).strip().strip('"').strip("'")
    return dict(kv.split("=", 1) for kv in cs.split(";") if "=" in kv)


def _sql_list(names: list[str]) -> str:
    # Names are our own constants (no quotes in them); still, double any single
    # quote defensively rather than trusting that forever.
    return ", ".join("'" + n.replace("'", "''") + "'" for n in names)


def main():
    p = argparse.ArgumentParser(description="Remove the demo „cărți” seed_demo.py created.")
    p.add_argument("--env", choices=sorted(ENV_FILES), help="Which database (reads its api env file).")
    p.add_argument("--db-url", help="Explicit Host=..;Database=..;Username=..;Password=.. instead of --env.")
    p.add_argument("--container", default=CONTAINER, help=f"Postgres container name (default {CONTAINER}).")
    p.add_argument("--apply", action="store_true", help="Actually delete. Without it, this is a dry run.")
    p.add_argument("--yes-this-is-production", action="store_true",
                   help="Required together with --env prod. Removing from prod is a deliberate act.")
    p.add_argument("--force-booked", action="store_true",
                   help="Delete even demo slots that have bookings (removes the bookings too). "
                        "Default is to refuse if any booking exists.")
    args = p.parse_args()

    if args.env == "prod" and not args.yes_this_is_production:
        sys.exit("Refusing to touch PRODUCTION without --yes-this-is-production.")
    if args.db_url:
        conn = dict(kv.split("=", 1) for kv in args.db_url.split(";") if "=" in kv)
    elif args.env:
        conn = _conn(ENV_FILES[args.env])
    else:
        sys.exit("Give --env {stage,prod} or --db-url.")

    db, user, pw = conn.get("Database"), conn.get("Username"), conn.get("Password")

    def q(sql: str) -> str:
        r = subprocess.run(
            ["docker", "exec", "-e", f"PGPASSWORD={pw}", args.container,
             "psql", "-U", user, "-d", db, "-v", "ON_ERROR_STOP=1", "-tAc", sql],
            capture_output=True, text=True)
        if r.returncode != 0:
            sys.exit(f"psql failed: {r.stderr.strip() or r.stdout.strip()}")
        return r.stdout.strip()

    names_sql = _sql_list(NAMES)

    # What is actually present of the seed's set.
    found = q(f"SELECT COUNT(*) FROM specialists WHERE full_name IN ({names_sql});")
    if found == "0":
        print(f"Nothing to remove — none of the {len(NAMES)} demo „cărți” are present in {db}.")
        return

    es = q(f"SELECT COUNT(*) FROM event_specialists WHERE specialist_id IN "
           f"(SELECT id FROM specialists WHERE full_name IN ({names_sql}));")
    slots = q(f"SELECT COUNT(*) FROM slots WHERE event_specialist_id IN "
              f"(SELECT id FROM event_specialists WHERE specialist_id IN "
              f"(SELECT id FROM specialists WHERE full_name IN ({names_sql})));")
    booked = q(f"SELECT COUNT(*) FROM bookings WHERE slot_id IN "
               f"(SELECT id FROM slots WHERE event_specialist_id IN "
               f"(SELECT id FROM event_specialists WHERE specialist_id IN "
               f"(SELECT id FROM specialists WHERE full_name IN ({names_sql}))));")

    print(f"Database {db}:")
    print(f"  demo „cărți”:        {found} / {len(NAMES)}")
    print(f"  roster rows:         {es}")
    print(f"  slots:               {slots}")
    print(f"  bookings on them:    {booked}")

    if booked != "0" and not args.force_booked:
        sys.exit(f"\nRefusing: {booked} slot(s) are booked — a real person is expecting those "
                 f"meetings. Deleting the slots deletes the bookings. Re-run with --force-booked "
                 f"only if you are sure.")

    if not args.apply:
        print("\nDRY RUN — nothing deleted. Re-run with --apply to remove the above.")
        return

    # Order matters: event_specialists -> specialist is RESTRICT, so the roster
    # rows (and, by cascade, their slots/patterns/bookings) go first, then the
    # specialists. Finally any demo- event left empty. One transaction.
    tx = f"""
BEGIN;
DELETE FROM event_specialists WHERE specialist_id IN
    (SELECT id FROM specialists WHERE full_name IN ({names_sql}));
DELETE FROM specialists WHERE full_name IN ({names_sql});
DELETE FROM events e WHERE e.slug LIKE 'demo-%'
    AND NOT EXISTS (SELECT 1 FROM event_specialists es WHERE es.event_id = e.id);
COMMIT;
"""
    q(tx)
    remaining = q(f"SELECT COUNT(*) FROM specialists WHERE full_name IN ({names_sql});")
    print(f"\nRemoved. Demo „cărți” remaining: {remaining} (expected 0). "
          f"Empty demo- events were dropped; reused real events were left intact.")


if __name__ == "__main__":
    main()
