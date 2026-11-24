/*
 * The payment page of the sattle.me Bitcoin payment method: the time left, the copy buttons, and news
 * of the payment. Every few seconds it asks the shop's server where the payment is (the server checks
 * it itself, at most every few seconds); when what the page shows changed, it reloads the page. The
 * page works without this script: it then offers a button to check again.
 */
(function () {
  'use strict';

  var page = document.getElementById('sattleme-pay');
  if (!page) {
    return;
  }

  function reload() {
    window.location.reload();
  }

  // The time left, as m:ss; once over, the page shows what became of the invoice.
  var countdown = page.querySelector('[data-countdown]');
  var secondsLeft = parseInt(page.getAttribute('data-seconds-left') || '', 10);
  if (countdown && secondsLeft >= 0) {
    var end = Date.now() + (secondsLeft * 1000);
    var tick = function () {
      var left = Math.max(0, Math.round((end - Date.now()) / 1000));
      var seconds = left % 60;
      countdown.textContent = Math.floor(left / 60) + ':' + (seconds < 10 ? '0' : '') + seconds;
      if (left === 0) {
        window.clearInterval(timer);
        window.setTimeout(reload, 2000);
      }
    };
    var timer = window.setInterval(tick, 1000);
    tick();
  }

  // The copy buttons: shown only here, where they work.
  var copied = page.getAttribute('data-copied') || '';
  Array.prototype.forEach.call(page.querySelectorAll('[data-copy]'), function (button) {
    var field = document.getElementById(button.getAttribute('data-copy'));
    if (!field) {
      return;
    }
    var label = button.textContent;
    var done = function () {
      button.textContent = copied;
      window.setTimeout(function () { button.textContent = label; }, 2000);
    };
    button.hidden = false;
    button.addEventListener('click', function () {
      if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(field.value).then(done, function () {
          field.select();
        });
        return;
      }
      field.select();
      try {
        if (document.execCommand('copy')) {
          done();
        }
      } catch (e) {
        // Selected: the customer copies it.
      }
    });
  });

  // News of the payment.
  var statusUrl = page.getAttribute('data-status-url');
  var version = page.getAttribute('data-version');
  var poll = parseInt(page.getAttribute('data-poll') || '', 10);
  if (!statusUrl || !(poll > 0) || !window.fetch) {
    return;
  }
  Array.prototype.forEach.call(page.querySelectorAll('.sattleme-pay-updates'), function (notice) {
    notice.hidden = false;
  });

  var failures = 0;
  var ask = function () {
    window.fetch(statusUrl, {
      method: 'GET',
      credentials: 'same-origin',
      cache: 'no-store',
      headers: { 'Accept': 'application/json' }
    }).then(function (response) {
      if (response.status === 404) {
        throw new Error('gone');
      }
      if (!response.ok) {
        throw new Error('status ' + response.status);
      }
      return response.json();
    }).then(function (status) {
      failures = 0;
      if (status && status.version && status.version !== version) {
        reload();
        return;
      }
      window.setTimeout(ask, poll * 1000);
    }).catch(function (error) {
      if (error && error.message === 'gone') {
        return;
      }
      // The shop is unreachable for a moment: ask again, less often.
      failures += 1;
      window.setTimeout(ask, Math.min(60, poll * Math.pow(2, Math.min(failures, 5))) * 1000);
    });
  };
  window.setTimeout(ask, poll * 1000);
}());
