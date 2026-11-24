"""What the end-to-end test reads in the shop's database once the module is uninstalled (e2e/run.sh
copies Smartstore's SQLite files out of its container): no error of the module in Smartstore's log
(but the 404s and the one refused uninstallation the test asks for on purpose), each order an
invoice paid noted once (never "paid twice"), the order with nothing to ship that the scheduled
task alone paid completed, none of its texts, settings or scheduled task left, and its invoices
kept.

    python3 -I e2e/database.py <Smartstore.db>
"""

import sqlite3
import sys

# The refusal drive.py's uninstall(expect_refusal=True) asks for, with its one invoice still payable:
# Smartstore 6.4.0 logs it as an error itself (ModuleController.ExecuteTasks catches it, NotifyError
# logs it), and the copy run.sh makes holds that entry in some runs and not in others. Once at most,
# this very message: any other error of the module, or a second refusal, still fails the test.
EXPECTED_REFUSAL = (
    "The module cannot be uninstalled while 1 Bitcoin invoice may still be paid or is being "
    "confirmed: wait until it is settled."
)


def main(path):
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    problems = []
    errors = [
        message
        for (message,) in db.execute(
            "select ShortMessage from Log where LogLevelId >= 40 and (ShortMessage like '%sattle%' or FullMessage like '%Sattleme%')"
        )
    ]
    if errors.count(EXPECTED_REFUSAL) == 1:
        errors.remove(EXPECTED_REFUSAL)
    problems += [f"the module logged an error: {message[:300]}" for message in errors]
    texts = db.execute("select count(*) from LocaleStringResource where ResourceName like '%Sattleme%'").fetchone()[0]
    if texts:
        problems.append(f"{texts} text(s) of the module remain after its uninstallation")
    settings = db.execute("select count(*) from Setting where Name like 'SattlemeSettings%'").fetchone()[0]
    if settings:
        problems.append(f"{settings} setting(s) of the module remain after its uninstallation")
    tasks = db.execute("select count(*) from ScheduleTask where Type = 'SattlemeCheckTask'").fetchone()[0]
    if tasks:
        problems.append("the module's scheduled task remains after its uninstallation")
    # Each order an invoice paid: its payment's note once, never a "paid twice"; one with nothing to
    # ship (ShippingStatus 10, ShippingNotRequired) completed by Smartstore (OrderStatus 30).
    for order_id, reference, order_status, shipping_status in db.execute(
            "select Id, CaptureTransactionId, OrderStatusId, ShippingStatusId from \"Order\" where CaptureTransactionId like 'ss-%'"):
        notes = [note for (note,) in db.execute("select Note from OrderNote where OrderId = ? and Note like ?", (order_id, f"%{reference}%"))]
        paid_notes = [note for note in notes if " paid with Lightning: " in note]
        if len(paid_notes) != 1:
            problems.append(f"the order {order_id} has {len(paid_notes)} payment note(s) of {reference}")
        if any("already paid otherwise" in note for note in notes):
            problems.append(f"the order {order_id} has a paid-twice note")
        if shipping_status == 10 and order_status != 30:
            problems.append(f"the order {order_id}, with nothing to ship, is not complete ({order_status})")
    completed = db.execute("select count(*) from \"Order\" where CaptureTransactionId like 'ss-%' and OrderStatusId = 30 and ShippingStatusId = 10").fetchone()[0]
    if completed < 1:
        problems.append("no order with nothing to ship was completed")
    invoices, paid = db.execute("select count(*), sum(State = 'paid') from SattlemeInvoice").fetchone()
    if invoices < 5 or paid < 4:
        problems.append(f"the invoices are not kept: {invoices} invoice(s), {paid} paid")
    for problem in problems:
        print(f"[e2e] {problem}", file=sys.stderr)
    if problems:
        sys.exit(1)
    print(f"[e2e] the database: {invoices} invoices kept ({paid} paid), each paid order's payment noted once, "
          f"{completed} order(s) with nothing to ship completed, no text, setting or task of the module left, no error logged")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
