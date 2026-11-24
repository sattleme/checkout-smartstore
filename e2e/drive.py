"""The end-to-end test of the sattle.me module in a real Smartstore 6.4.0 (e2e/run.sh starts it).

It does what a merchant and a customer do, over HTTP, as a browser would: installs Smartstore on
SQLite with its sample data, logs in as its admin, uploads the module's package and the test-only
fake of the SDK (Sattleme.E2e), installs both, sets an integration key and activates the payment
method, then orders a product with it, is sent to the payment page, checks it (QR code, wallet
link, no inline script of its own, never cached nor indexed, refused without its secret), has the
fake pay the invoice, and sees the order paid: once by the page's status, once by the background
checks alone, for an order of a download that Smartstore then completes (and e-mails) from the
scheduled task's request; and asks for a new invoice once one ended. Last, it uninstalls the
module.

Python's standard library only; run with `python3 -I`.
"""

import argparse
import html
import html.parser
import http.cookiejar
import json
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

KEY = (
    "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o."
    "uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkuZXhhbXBsZQ"
)
ADMIN_EMAIL = "admin@example.com"
ADMIN_PASSWORD = "e2e-Admin-123!"


class Failure(Exception):
    """What the test found wrong."""


def check(condition, message):
    if not condition:
        raise Failure(message)


def step(message):
    print(f"[e2e] {message}", flush=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Response:
    def __init__(self, status, headers, body):
        self.status = status
        self.headers = headers
        self.body = body

    @property
    def text(self):
        return self.body.decode("utf-8", "replace")

    def json(self):
        return json.loads(self.text)

    @property
    def location(self):
        return self.headers.get("Location")


class Browser:
    """A browser of the shop: cookies, the shop's host name, no redirect followed unless asked."""

    def __init__(self, base, host):
        self.base = base.rstrip("/")
        self.host = host
        self.jar = http.cookiejar.CookieJar()
        # The shop is on this machine: no proxy.
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(self.jar),
            NoRedirect(),
        )

    def url(self, path):
        if path.startswith("http://") or path.startswith("https://"):
            parsed = urllib.parse.urlsplit(path)
            path = parsed.path + (("?" + parsed.query) if parsed.query else "")
        return self.base + path

    def request(self, method, path, data=None, headers=None, timeout=120, follow=False):
        body = None
        all_headers = {"Host": self.host, "User-Agent": "Mozilla/5.0 (sattleme-e2e)", "Accept-Language": "en-US"}
        all_headers.update(headers or {})
        if isinstance(data, (dict, list)):
            body = urllib.parse.urlencode(data, doseq=True).encode()
            all_headers.setdefault("Content-Type", "application/x-www-form-urlencoded")
        elif isinstance(data, bytes):
            body = data
        request = urllib.request.Request(self.url(path), data=body, headers=all_headers, method=method)
        try:
            with self.opener.open(request, timeout=timeout) as answer:
                response = Response(answer.status, answer.headers, answer.read())
        except urllib.error.HTTPError as error:
            response = Response(error.code, error.headers, error.read())
        if follow and response.status in (301, 302, 303, 307, 308):
            return self.request("GET", response.location, timeout=timeout, follow=True)
        return response

    def get(self, path, **options):
        return self.request("GET", path, **options)

    def post(self, path, data, **options):
        return self.request("POST", path, data=data, **options)

    def multipart(self, path, fields, file_field, file_name, content):
        boundary = uuid.uuid4().hex
        parts = []
        for name, value in fields.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; filename="{file_name}"\r\n'
            "Content-Type: application/zip\r\n\r\n".encode() + content + b"\r\n"
        )
        parts.append(f"--{boundary}--\r\n".encode())
        return self.request("POST", path, data=b"".join(parts),
                            headers={"Content-Type": f"multipart/form-data; boundary={boundary}"}, timeout=300)


class Forms(html.parser.HTMLParser):
    """The forms of a page: their action and fields, as a browser would post them."""

    def __init__(self):
        super().__init__()
        self.forms = []
        self.current = None
        self.textarea = None
        self.select = None

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if tag == "form":
            self.current = {"action": attributes.get("action", ""), "id": attributes.get("id", ""), "fields": []}
            self.forms.append(self.current)
        elif self.current is None:
            return
        elif tag == "input":
            kind = (attributes.get("type") or "text").lower()
            name = attributes.get("name")
            if not name or kind in ("submit", "button", "file", "image"):
                return
            if kind in ("checkbox", "radio") and "checked" not in attributes:
                return
            self.current["fields"].append((name, attributes.get("value") or ("true" if kind == "checkbox" else "")))
        elif tag == "textarea" and attributes.get("name"):
            self.textarea = [attributes["name"], ""]
        elif tag == "select" and attributes.get("name"):
            # [name, selected values, first value, multiple]
            self.select = [attributes["name"], [], None, "multiple" in attributes]
        elif tag == "option" and self.select is not None:
            value = attributes.get("value", "")
            if self.select[2] is None:
                self.select[2] = value
            if "selected" in attributes:
                self.select[1].append(value)

    def handle_data(self, data):
        if self.textarea is not None:
            self.textarea[1] += data

    def handle_endtag(self, tag):
        if tag == "textarea" and self.textarea is not None and self.current is not None:
            self.current["fields"].append((self.textarea[0], html.unescape(self.textarea[1])))
            self.textarea = None
        elif tag == "select" and self.select is not None and self.current is not None:
            name, selected, first, multiple = self.select
            if multiple:
                # A list: what is selected, nothing else.
                self.current["fields"].extend((name, value) for value in selected)
            else:
                self.current["fields"].append((name, selected[-1] if selected else (first or "")))
            self.select = None
        elif tag == "form":
            self.current = None


def forms_of(page):
    parser = Forms()
    parser.feed(page)
    return parser.forms


def form_with(page, action_part):
    for form in forms_of(page):
        if action_part.lower() in form["action"].lower():
            return form
    raise Failure(f"no form posting to {action_part}")


def token_of(page):
    """The page's antiforgery token: a form's field, or the meta tag Smartstore's scripts send as
    the X-XSRF-Token header (smartstore.common.js)."""
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page) or re.search(
        r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', page) or re.search(
        r"<meta name=['\"]__rvt['\"] content=['\"]([^'\"]+)['\"]", page)
    check(match is not None, "no antiforgery token in the page")
    return html.unescape(match.group(1))


def wait_until(what, condition, timeout, every=3):
    deadline = time.monotonic() + timeout
    while True:
        try:
            if condition():
                return
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            pass
        if time.monotonic() > deadline:
            raise Failure(f"timed out waiting for {what}")
        time.sleep(every)


# -- Smartstore --


def install(browser):
    """Smartstore's installer, as its page posts it (Install/Install, then Install/Finalize)."""
    step("waiting for Smartstore's installer")
    wait_until("the installer", lambda: browser.get("/install").status == 200, 300)
    page = browser.get("/install").text
    fields = dict(form_with(page, "/install/install")["fields"])
    fields.update({
        "AdminEmail": ADMIN_EMAIL,
        "AdminPassword": ADMIN_PASSWORD,
        "ConfirmPassword": ADMIN_PASSWORD,
        "DataProvider": "sqlite",
        "UseRawConnectionString": "false",
        "DbName": "Smartstore",
        "MediaStorage": "fs",
        "InstallSampleData": "true",
        "UseCustomCollation": "false",
    })
    step(f"installing Smartstore 6.4.0 with its sample data on SQLite ({fields.get('PrimaryLanguage')})")
    answer = browser.post("/install/install", fields, headers={"X-Requested-With": "XMLHttpRequest"}, timeout=1800)
    check(answer.status == 200, f"the installation answered {answer.status}")
    result = answer.json()
    check(result.get("Success") and result.get("Completed"), f"the installation failed: {result.get('Errors')}")
    step("restarting Smartstore after its installation")
    try:
        browser.post("/install/finalize", {"restart": "true"}, headers={"X-Requested-With": "XMLHttpRequest"}, timeout=60)
    except OSError:
        pass
    time.sleep(5)
    wait_until("the shop", lambda: browser.get("/").status == 200, 600, every=5)


def submit(browser, page, action, values=None, extra=None, **options):
    """Posts the form of <page> whose action contains <action>, as a browser would, with <values>
    in place of its fields' and <extra> added."""
    form = form_with(page, action)
    values = values or {}
    fields = [(name, value) for name, value in form["fields"] if name not in values]
    for name, value in values.items():
        fields.extend((name, item) for item in (value if isinstance(value, list) else [value]))
    fields.extend(extra or [])
    return browser.post(form["action"], fields, **options)


def login(browser):
    page = browser.get("/login").text
    answer = submit(browser, page, "/login", {"UsernameOrEmail": ADMIN_EMAIL, "Password": ADMIN_PASSWORD, "RememberMe": "false"})
    check(answer.status == 302, f"the admin's login failed: {answer.status}")
    check(browser.get("/admin", follow=True).status == 200, "the admin area is not open to the admin")


def restart(browser, ready):
    """Admin → Maintenance → Restart application (MaintenanceController.RestartApplication, POST):
    Smartstore stops, Docker starts it again; then waits for <ready>."""
    page = browser.get("/admin/module/list").text
    try:
        browser.post("/admin/maintenance/restartapplication", {}, headers={"X-XSRF-Token": token_of(page), "X-Requested-With": "XMLHttpRequest"}, timeout=60)
    except OSError:
        pass
    time.sleep(5)
    wait_until("the shop after its restart", ready, 600, every=5)


def modules(browser):
    """The modules of Admin → Plugins → Manage plugins: system name -> installed (its card offers
    Uninstall) or not (Install)."""
    page = browser.get("/admin/module/list")
    if page.status != 200:
        return {}
    found = {}
    for card in page.text.split('class="module-item card')[1:]:
        name = re.search(r'data-module="([^"]+)"', card)
        if name:
            found[name.group(1)] = "module-uninstall" in card
    return found


def upload_and_install(browser, packages):
    for path in packages:
        page = browser.get("/admin/module/list").text
        with open(path, "rb") as package:
            content = package.read()
        name = path.rsplit("/", 1)[-1]
        step(f"uploading {name} (Plugins, Manage plugins, Upload plugin)")
        answer = browser.multipart("/admin/packaging/uploadpackage?expectTheme=False", {"__RequestVerificationToken": token_of(page)},
                                   "file", name, content)
        check(answer.status == 200 and answer.json().get("success") is True, f"the upload failed: {answer.status} {answer.text[:300]}")
    step("restarting Smartstore to load the uploaded modules")
    restart(browser, lambda: "Sattleme.E2e" in modules(browser))
    listed = modules(browser)
    check(listed.get("Sattleme.Bitcoin") is False and listed.get("Sattleme.E2e") is False, f"the modules are not listed to install: {listed}")
    page = browser.get("/admin/module/list").text
    step("installing Sattleme.Bitcoin and Sattleme.E2e (Install, then Smartstore restarts)")
    answer = submit(browser, page, "/admin/module/executetasks", {"modulesToInstall": ["Sattleme.Bitcoin", "Sattleme.E2e"]})
    check(answer.status == 302 and "restartapplication" in (answer.location or "").lower(), f"installing failed: {answer.status} {answer.location}")
    restart(browser, lambda: modules(browser).get("Sattleme.Bitcoin") is True and modules(browser).get("Sattleme.E2e") is True)


def configure(browser):
    """The module's configuration page: the integration key saved (verified by the SDK, then
    encrypted), never shown again; then the payment method activated (Configuration → Payment
    methods, PaymentController.ActivateProvider)."""
    page = browser.get("/admin/sattlemeadmin/configure").text
    check("Bitcoin" in page and 'name="IntegrationKey"' in page, "the configuration page is not the module's")
    step("saving the integration key (verified by the SDK, then encrypted)")
    answer = submit(browser, page, "/admin/sattlemeadmin/configure", {"IntegrationKey": KEY, "MinutesToPay": "15"})
    check(answer.status == 302, f"the settings were not saved: {answer.status} {answer.text[:500]}")
    page = browser.get("/admin/sattlemeadmin/configure").text
    check(KEY not in page and KEY[12:40] not in page, "the configuration page shows the key")
    check("A key is saved." in page, "the configuration page does not say the key is saved")
    check(re.search(r'name="IntegrationKey"[^>]*value="[^"]+"', page) is None, "the key field has a value")
    check(re.search(r'<input[^>]*type="password"[^>]*name="IntegrationKey"|<input[^>]*name="IntegrationKey"[^>]*type="password"', page) is not None,
          "the key field is not a password field")
    step("activating the payment method (Configuration, Payment methods)")
    providers = browser.get("/admin/payment/providers").text
    answer = browser.post("/admin/payment/activateprovider", {"systemName": "Payments.Sattleme", "activate": "true"},
                          headers={"X-XSRF-Token": token_of(providers)})
    check(answer.status == 302, f"activating the method answered {answer.status}")


def checkout_step(browser, path, values=None):
    """A step of Smartstore's checkout: its page's form posted with its Next button (nextstep)."""
    page = browser.get(path)
    check(page.status == 200, f"{path} answered {page.status} {page.location}")
    answer = submit(browser, page.text, path, values, extra=[("nextstep", "Next")])
    check(answer.status == 302, f"{path} did not go on: {answer.status} {re.findall(r'alert-danger[^>]*>(.*?)</', answer.text, re.S)[:2]}")
    return answer


def place_order(browser, product):
    """The customer (the admin, whose address the sample data has) buys <product>: Smartstore's
    checkout, step by step, with the Bitcoin payment method; then is sent to the payment page."""
    answer = browser.post(f"/cart/addproductsimple/{product}", {}, headers={"X-Requested-With": "XMLHttpRequest"})
    check(answer.status == 200 and answer.json().get("success"), f"the product was not added to the cart: {answer.text[:300]}")
    location = browser.get("/checkout").location or ""
    while "/checkout/" in location.lower() and not location.lower().rstrip("/").endswith("/checkout/paymentmethod") \
            and not location.lower().rstrip("/").endswith("/checkout/confirm"):
        path = urllib.parse.urlsplit(location).path
        page = browser.get(path).text
        # An address step that offers the customer's saved address (after an order of a download,
        # which had none): its "Ship to this address" button posts its data-url, as Smartstore's
        # script does (Views/Checkout/Partials/AddressScript.cshtml, postData).
        select = re.search(r'class="[^"]*select-(?:shipping|billing)-address-button"\s+data-url="([^"]+)"', page)
        if select:
            answer = browser.post(html.unescape(select.group(1)), {"__RequestVerificationToken": token_of(page), "shippingAddressDiffers": "false"})
            check(answer.status == 302, f"{path}: the saved address was not selected: {answer.status}")
            location = answer.location or ""
            continue
        location = checkout_step(browser, path).location or ""
    page = browser.get("/checkout/paymentmethod").text
    check('value="Payments.Sattleme"' in page, "the Bitcoin payment method is not offered at checkout")
    check("Pay in bitcoin" in page, "the payment step does not describe the method")
    location = checkout_step(browser, "/checkout/paymentmethod", {"paymentmethod": "Payments.Sattleme"}).location or ""
    check("/checkout/confirm" in location.lower(), f"after the payment method: {location}")
    page = browser.get("/checkout/confirm").text
    answer = submit(browser, page, "/checkout/confirm", {"AcceptThirdPartyEmailHandOver": "false"})
    check(answer.status == 302, f"the order was not placed: {answer.status} {re.findall(r'alert-danger[^>]*>(.*?)</', answer.text, re.S)[:2]}")
    location = answer.location or ""
    check(re.match(r"^/sattleme/pay/[0-9a-f-]{36}/?\?token=[0-9a-f]{64}$", location) is not None, f"not the module's payment page: {location}")
    return location


def status_path(location):
    return location.replace("/sattleme/pay/", "/sattleme/status/")


def check_page(browser, location):
    """The payment page as the customer's browser gets it; returns the order's id."""
    page = browser.get(location)
    check(page.status == 200, f"the payment page answered {page.status}")
    check("no-store" in (page.headers.get("Cache-Control") or ""), "the payment page may be cached")
    check("noindex" in (page.headers.get("X-Robots-Tag") or ""), "the payment page may be indexed")
    check(page.headers.get("Referrer-Policy") == "no-referrer", "the payment page sends its address as referrer")
    text = page.text
    check(re.search(r"<meta name=['\"]robots['\"] content=['\"]noindex, nofollow['\"]", text) is not None, "no robots meta")
    body = text[text.index('id="sattleme-pay"'):]
    body = body[:body.index("sattleme-pay-back")]
    check("<script" not in body, "the payment page has a script of its own inline")
    check('<script src="/Modules/Sattleme.Bitcoin/pay.js">' in text, "the page's script is not the module's file")
    check("<svg" in body and 'role="img"' in body, "no QR code")
    check(re.search(r'href="bitcoin:bc1p[^"]*lightning=lnbc', body) is not None, "no wallet link")
    check("lnbc" in body and "bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e" in body, "the invoice or the address is not shown to copy")
    check("check=1" in body and "<noscript>" in body, "no check without JavaScript")
    check(" sats</strong>" in body and re.search(r"<strong data-countdown>1[45]:\d\d</strong>", body) is not None, "the amount or the time left is missing")
    script = browser.get("/Modules/Sattleme.Bitcoin/pay.js")
    check(script.status == 200 and b"sattleme-pay" in script.body, "the page's script is not served")
    wrong = location[:-1] + ("0" if location[-1] != "0" else "1")
    check(browser.get(wrong).status == 404, "the payment page opens with another secret")
    check(browser.get(location.split("?")[0]).status == 404, "the payment page opens without its secret")
    status = browser.get(status_path(location))
    check(status.status == 200 and status.json()["phase"] == "open", f"the status is not open: {status.text}")
    order = re.search(r'href="/order/details/(\d+)/?"', text)
    check(order is not None, "no link back to the order")
    return int(order.group(1))


def references(browser):
    return browser.get("/e2e-sattleme/references").json()


def pay(browser, reference):
    check(re.match(r"^ss-[0-9a-f]{32}$", reference) is not None, f"not a random reference: {reference}")
    check(browser.post(f"/e2e-sattleme/pay/{reference}", {}).status == 200, "the fake could not pay")


def admin_order(browser, order_id):
    """The admin's order page, and its Bitcoin tab (loaded by Ajax: SattlemeAdmin/OrderPayment)."""
    page = browser.get(f"/admin/order/edit/{order_id}")
    check(page.status == 200, f"the admin's order page answered {page.status}")
    tab = browser.get(f"/admin/sattlemeadmin/orderpayment?orderId={order_id}", headers={"X-Requested-With": "XMLHttpRequest"})
    check(tab.status == 200, f"the order's Bitcoin tab answered {tab.status}")
    return page.text, tab.text


def paid_in_admin(page):
    """The admin's order page says its payment status is Paid."""
    return re.search(r"Payment status.{0,400}?>\s*Paid\s*<", page, re.S) is not None


def complete_in_admin(page):
    """The admin's order page says its order status is Complete."""
    return re.search(r'id="OrderStatusString"[^>]*value="Complete"', page) is not None


def product_id(browser, name):
    """The id of the sample data's product <name>, from its page (found by the shop's search)."""
    search = browser.get("/search?q=" + urllib.parse.quote(name)).text
    link = re.search(r'href="(/[a-z0-9-]*vivaldi[a-z0-9-]*/?)"', search)
    check(link is not None, f"no product {name} in the shop's search")
    page = browser.get(link.group(1)).text
    found = re.search(r"/cart/addproduct/(\d+)", page) or re.search(r'data-product-id="(\d+)"', page) \
        or re.search(r'name="ProductId"[^>]*value="(\d+)"', page)
    check(found is not None, f"no product id on the page of {name}")
    return int(found.group(1))


def order_block(browser, order_id):
    """The module's block on the customer's page of the order (My account, Orders), as text, then its links."""
    page = browser.get(f"/order/details/{order_id}")
    check(page.status == 200, f"the customer's order page answered {page.status}")
    block = re.search(r'<div class="card card-body mb-3 sattleme-order-payment">(.*?)</div>', page.text, re.S)
    check(block is not None, "the customer's order page has no Bitcoin payment block")
    text = re.sub(r"<[^>]+>", " ", block.group(1))
    links = " ".join(re.findall(r'href="([^"]+)"', block.group(1)))
    return html.unescape(re.sub(r"\s+", " ", text).replace(" :", ":").replace(" .", ".")).strip() + " " + html.unescape(links)


def set_minutes(browser, minutes):
    page = browser.get("/admin/sattlemeadmin/configure").text
    answer = submit(browser, page, "/admin/sattlemeadmin/configure", {"MinutesToPay": str(minutes)})
    check(answer.status == 302, f"the time to pay was not saved: {answer.status}")


def new_invoice(browser, location):
    """An invoice that ended unpaid: the page offers a new one, which only its form (with its
    antiforgery token) gets."""
    wait_until("the invoice's end (its time, its grace period, then a check)",
               lambda: browser.get(status_path(location)).json()["phase"] == "expired", 420, every=10)
    page = browser.get(location).text
    check("This invoice was not paid in time." in page and "Get a new invoice" in page, "the page does not offer a new invoice")
    form = next((form for form in forms_of(page) if "/sattleme/invoice/" in form["action"]), None)
    check(form is not None, "the page has no form for a new invoice")
    check(any(name == "__RequestVerificationToken" for name, _ in form["fields"]), "the new invoice's form has no antiforgery token")
    before = len(references(browser))
    refused = browser.post(form["action"], [(name, value) for name, value in form["fields"] if name != "__RequestVerificationToken"])
    check(refused.status == 400 and len(references(browser)) == before, f"a new invoice without the form's token: {refused.status}")
    answer = browser.post(form["action"], form["fields"])
    check(answer.status == 302 and "/sattleme/pay/" in (answer.location or ""), f"the new invoice's form answered {answer.status} {answer.location}")
    check(len(references(browser)) == before + 1, "no new invoice was made")
    check(browser.get(status_path(location)).json()["phase"] == "open", "the new invoice is not open")


def uninstall(browser, expect_refusal):
    page = browser.get("/admin/module/list").text
    answer = submit(browser, page, "/admin/module/executetasks", {"modulesToUninstall": ["Sattleme.Bitcoin"]})
    if expect_refusal:
        check(answer.status == 302 and "restartapplication" not in (answer.location or "").lower(), f"uninstalling answered {answer.status} {answer.location}")
        listed = browser.get("/admin/module/list").text
        check("cannot be uninstalled while 1 Bitcoin invoice may still be paid or is being confirmed" in html.unescape(listed), "Smartstore does not say why the module stays")
        check(modules(browser).get("Sattleme.Bitcoin") is True, "the module was uninstalled while a payment may still come")
        return
    check(answer.status == 302 and "restartapplication" in (answer.location or "").lower(), f"uninstalling failed: {answer.status} {answer.location}")
    restart(browser, lambda: modules(browser).get("Sattleme.Bitcoin") is False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--module", required=True)
    parser.add_argument("--fake", required=True)
    parser.add_argument("--product", type=int, default=13)
    args = parser.parse_args()
    browser = Browser(args.base, args.host)

    install(browser)
    login(browser)
    upload_and_install(browser, [args.module, args.fake])
    configure(browser)

    step("placing an order with the Bitcoin payment method")
    location = place_order(browser, args.product)
    placed = time.monotonic()
    step(f"on the payment page {location[:30]}…")
    order_id = check_page(browser, location)
    check("waiting for your payment" in order_block(browser, order_id), "the customer's order page does not say the payment is awaited")
    reference = references(browser)[-1]
    pay(browser, reference)
    step(f"the fake paid {reference}; the page's status checks it")
    time.sleep(6)
    status = browser.get(status_path(location)).json()
    check(status["phase"] == "paid", f"the status after the payment is {status}")
    check("Thank you: your order is paid." in browser.get(location).text, "the payment page does not say the order is paid")
    page, tab = admin_order(browser, order_id)
    check("Bitcoin payment (sattle.me)" in page, "the admin's order page has no Bitcoin tab")
    check(reference in tab and "Proof of payment (preimage)" in tab, "the order's Bitcoin tab does not show the reference and the proof")
    check(KEY not in tab and KEY not in page, "an admin page shows the key")
    check(paid_in_admin(page), "the order is not paid in the admin area")
    check("Bitcoin payment: paid." in order_block(browser, order_id), "the customer's order page does not say it is paid")
    step(f"order {order_id} is paid, its capture transaction {reference}")

    # A product with nothing to ship (a download): Smartstore completes its order as soon as it is
    # paid, and sends its "order completed" e-mail, from the scheduled task's own request.
    step("a second order, of a download, paid with no page open: the background checks complete it")
    download = product_id(browser, "Antonio Vivaldi")
    time.sleep(max(0.0, 35 - (time.monotonic() - placed)))
    location = place_order(browser, download)
    placed = time.monotonic()
    second = check_page(browser, location)
    block = order_block(browser, second)
    check("waiting for your payment" in block and html.unescape(location) in block, f"the customer's order page does not lead to the payment page: {block}")
    pay(browser, references(browser)[-1])
    wait_until("the background check (every minute)", lambda: paid_in_admin(admin_order(browser, second)[0]), 240, every=10)
    wait_until("the order completed", lambda: complete_in_admin(admin_order(browser, second)[0]), 120, every=10)
    block = order_block(browser, second)
    check("Bitcoin payment: paid." in block and "/sattleme/pay/" not in block, f"the customer's order page after the payment: {block}")
    step(f"order {second} (product {download}) is paid and complete by the scheduled task alone")

    step("a third order, its invoice of one minute not paid: a new invoice from the payment page")
    set_minutes(browser, 1)
    time.sleep(max(0.0, 35 - (time.monotonic() - placed)))
    location = place_order(browser, args.product)
    new_invoice(browser, location)
    pay(browser, references(browser)[-1])
    wait_until("the new invoice paid", lambda: browser.get(status_path(location)).json()["phase"] == "paid", 60, every=5)
    step("the new invoice is paid")

    step("the module stays installed while a payment may still come, then is uninstalled")
    time.sleep(35)
    location = place_order(browser, args.product)
    uninstall(browser, expect_refusal=True)
    pay(browser, references(browser)[-1])
    wait_until("the last invoice paid", lambda: browser.get(status_path(location)).json()["phase"] == "paid", 60, every=5)
    uninstall(browser, expect_refusal=False)
    step("uninstalled")
    print("[e2e] passed", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Failure as failure:
        print(f"[e2e] FAILED: {failure}", file=sys.stderr, flush=True)
        sys.exit(1)
