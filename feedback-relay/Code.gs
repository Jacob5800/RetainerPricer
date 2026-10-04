/**
 * Retainer Pricer feedback relay.
 *
 * Set FEEDBACK_RECIPIENT in this script's Script Properties before deployment.
 * The recipient is intentionally not stored in the public plugin repository.
 */
function doPost(e) {
  try {
    const properties = PropertiesService.getScriptProperties();
    const recipient = String(properties.getProperty('FEEDBACK_RECIPIENT') || '').trim();
    if (!recipient || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(recipient)) {
      return json_({ ok: false, error: 'Feedback delivery is not configured.' });
    }

    const body = JSON.parse(e && e.postData && e.postData.contents || '{}');
    const message = String(body.message || '').trim();
    const pluginVersion = String(body.pluginVersion || 'unknown').slice(0, 32);
    if (message.length < 5 || message.length > 4000) {
      return json_({ ok: false, error: 'Feedback must be between 5 and 4000 characters.' });
    }

    // Protect the owner's mailbox and Apps Script email quota from accidental spam.
    // This is intentionally a conservative shared daily cap for the public endpoint.
    const day = Utilities.formatDate(new Date(), 'Etc/UTC', 'yyyyMMdd');
    const countKey = 'FEEDBACK_COUNT_' + day;
    const lock = LockService.getScriptLock();
    lock.waitLock(5000);
    let count;
    try {
      count = Number(properties.getProperty(countKey) || '0');
      if (count >= 25) return json_({ ok: false, error: 'Feedback is temporarily at its daily limit. Please try again tomorrow.' });
      properties.setProperty(countKey, String(count + 1));
    } finally {
      lock.releaseLock();
    }

    MailApp.sendEmail({
      to: recipient,
      subject: 'Retainer Pricer feedback (' + pluginVersion + ')',
      body: 'Feedback from Retainer Pricer ' + pluginVersion + ':\n\n' + message + '\n\nSent at (UTC): ' + new Date().toISOString(),
    });
    return json_({ ok: true });
  } catch (error) {
    console.error('Feedback relay failed: ' + String(error));
    return json_({ ok: false, error: 'Feedback could not be sent. Please try again later.' });
  }
}

function json_(value) {
  return ContentService.createTextOutput(JSON.stringify(value))
    .setMimeType(ContentService.MimeType.JSON);
}
