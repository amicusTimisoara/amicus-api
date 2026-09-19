#!/usr/bin/env python3
"""Seed a demo catalogue: one „carte” for every story profile, with availability.

For showing the app — a launch night, a committee meeting — not for real use.
The real „cărți” apply for themselves through /devino-carte.

    python scripts/seed_demo.py --email admin@amicus.local --password '...'
    python scripts/seed_demo.py --api https://stage.thorsp.net --email ... --password ...

Needs an account that already holds the Admin role. Safe to re-run: „cărți” are
matched by name, so a second run adds nothing and changes nothing.
"""

import argparse
import getpass
import json
import sys
import urllib.error
import urllib.request
from datetime import date, timedelta

sys.stdout.reconfigure(encoding="utf-8")

# One per StoryProfile value, so every tag in the catalogue has something behind
# it. Categories are spread deliberately: a demo where every dot is the same
# colour shows nothing about how the board reads.
# Invented people, with one exception: the pastor is Levis Nistor, who is really
# in the project — the brief lists him as one of the ten „cărți”. His line is an
# invitation rather than a claim about his life, because putting words in a real
# person's mouth is not ours to do.
CARTI = [
    ("Andrei Lupu", "Consilier antidrog", "Social", "FostDependent",
     "Zece ani în care am pierdut aproape tot. Acum stau de vorbă cu oameni care sunt exact acolo unde eram eu."),
    ("Cristina Barbu", "Antreprenor", "Cariera", "Antreprenor",
     "Am dat faliment de două ori înainte să meargă ceva. Despre ce înveți când nu merge."),
    ("Dr. Sorin Vasile", "Medic de familie", "Medical", "Medic",
     "Douăzeci de ani de cabinet într-un sat. Despre ce te învață oamenii când îi vezi toată viața."),
    ("Olena Kovalenko", "Traducătoare", "Social", "Refugiat",
     "Am ajuns în România cu o valiză și fără limbă. Despre cum îți construiești o casă a doua oară."),
    ("Marian Dobre", "Mentor de reintegrare", "Social", "FostDetinut",
     "Șapte ani după gratii și restul vieții încercând să nu fiu doar atât."),
    ("Vlad Ionescu", "Inginer software", "Spiritual", "FostAteu",
     "Am crezut că am un argument pentru orice. Despre ce se întâmplă când nu mai ai."),
    ("Elena Marinescu", "Asistentă medicală", "Social", "Tragedie",
     "Am pierdut pe cineva și am rămas în picioare. Nu e o poveste frumoasă, dar e adevărată."),
    ("Sorina Crețu", "Artist vizual", "Cariera", "Artist",
     "Despre cum trăiești din ceva ce toată lumea îți spune că nu e o meserie."),
    ("Dr. Radu Neagu", "Psiholog clinician", "Medical", "Psiholog",
     "Ascult oameni de cincisprezece ani. Despre ce aud cel mai des de la studenți."),
    ("Levis Nistor", "Pastor", "Spiritual", "Pastor",
     "Întrebările pe care nu le pui într-o biserică plină. Aici le poți pune."),
    ("Ioana Bălan", "Studentă la Medicină", "Mentorat", "FostOlimpic",
     "Am luat aur la olimpiadă și apoi m-am blocat complet. Despre ce vine după performanță."),
]

# Spread across the week and the afternoon, so the month grid shows a spread of
# days rather than one crowded column.
PATTERN_DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday",
                "Saturday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]
PATTERN_START = ["16:00:00", "17:00:00", "18:00:00", "16:30:00", "17:30:00",
                 "11:00:00", "18:30:00", "16:00:00", "19:00:00", "17:00:00", "18:00:00"]


class Api:
    def __init__(self, base, token=None):
        self.base = base.rstrip("/")
        self.token = token

    def __call__(self, path, body=None, method=None):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        headers = {"Content-Type": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        req = urllib.request.Request(
            self.base + path, data=data, headers=headers,
            method=method or ("POST" if data is not None else "GET"))
        try:
            with urllib.request.urlopen(req) as r:
                text = r.read().decode("utf-8")
                return r.status, (json.loads(text) if text else None)
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8")[:300]


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--api", default="http://localhost:5080")
    p.add_argument("--email", required=True)
    p.add_argument("--password", help="Omit it and you will be prompted, so it "
                                              "stays out of your shell history.")
    p.add_argument("--weeks", type=int, default=6,
                   help="How far ahead the demo event runs (default 6 weeks).")
    args = p.parse_args()

    password = args.password or getpass.getpass(f"Password for {args.email}: ")

    api = Api(args.api)
    status, body = api("/auth/login", {"email": args.email, "password": password})
    if status != 200:
        sys.exit(f"Could not sign in as {args.email}: {status} {body}")
    api.token = body["accessToken"]

    status, events = api("/admin/events")
    if status == 403:
        sys.exit(f"{args.email} signed in but is not an Admin on {args.api}.")
    if status != 200:
        sys.exit(f"Could not read events: {status} {events}")

    today = date.today()
    ends = today + timedelta(weeks=args.weeks)

    # Reuse an event that already covers today. Creating a second one that
    # overlaps would leave these „cărți” unable to publish later, because a date
    # would no longer identify one event.
    event = next(
        (e for e in events
         if date.fromisoformat(e["startsOn"]) <= today <= date.fromisoformat(e["endsOn"])),
        None)

    if event is None:
        status, event = api("/admin/events", {
            "name": f"Biblioteca Vie · {today.strftime('%B %Y')}",
            "slug": f"demo-{today:%Y-%m}",
            "startsOn": today.isoformat(),
            "endsOn": ends.isoformat(),
        })
        if status not in (200, 201):
            sys.exit(f"Could not create an event: {status} {event}")
        print(f"created event  {event['slug']}  {event['startsOn']} → {event['endsOn']}")
    else:
        print(f"using event    {event['slug']}  {event['startsOn']} → {event['endsOn']}")

    status, existing = api("/admin/specialists")
    by_name = {s["fullName"]: s for s in (existing or [])}

    created = skipped = 0
    for i, (name, specialty, category, profile, bio) in enumerate(CARTI):
        if name in by_name:
            skipped += 1
            continue

        status, specialist_id = api("/admin/specialists", {
            "fullName": name, "specialty": specialty, "bio": bio,
            "category": category, "profile": profile,
        })
        if status not in (200, 201):
            print(f"  ! {name}: {status} {specialist_id}")
            continue

        status, assignment_id = api(f"/admin/events/{event['id']}/specialists", {
            "specialistId": specialist_id, "location": "Sediul BETA",
        })
        if status not in (200, 201):
            print(f"  ! {name} not rostered: {status} {assignment_id}")
            continue

        # 30-minute slots with a 10-minute gap, which is what the brief describes.
        api(f"/admin/event-specialists/{assignment_id}/patterns", {
            "dayOfWeek": PATTERN_DAYS[i],
            "startTime": PATTERN_START[i],
            "endTime": f"{int(PATTERN_START[i][:2]) + 2:02d}{PATTERN_START[i][2:]}",
            "slotDurationMinutes": 30,
            "breakMinutes": 10,
        })

        created += 1
        print(f"  + {name:22} {category:10} {profile}")

    status, result = api(f"/admin/events/{event['id']}/generate-slots", {})
    print(f"\nslots          {result}" if status == 200 else f"\nslots failed   {status} {result}")

    status, _ = api(f"/admin/events/{event['id']}/publish", {})
    print(f"published      {'yes' if status in (200, 204) else f'failed {status}'}")
    print(f"\n{created} „cărți” created, {skipped} already there.")


if __name__ == "__main__":
    main()
