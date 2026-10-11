// Synthetic secret only. Ciphertext and a digest cross stdout; plaintext never leaves this process.
const { app, safeStorage } = require('electron')
const { randomBytes, createHash } = require('node:crypto')
const path = require('node:path')
const os = require('node:os')
const profile = process.env.THE_ORDER_PROBE_PROFILE
if (!profile || !path.isAbsolute(profile) || path.dirname(profile) !== os.tmpdir() || !path.basename(profile).startsWith('the-order-secret-probe-')) process.exit(2)
app.setPath('userData', profile)
app.whenReady().then(() => {
  if (!safeStorage.isEncryptionAvailable()) throw Error('Encryption unavailable')
  const secret = randomBytes(32).toString('hex')
  const encrypted = safeStorage.encryptString(secret)
  const roundtrip = safeStorage.decryptString(encrypted) === secret
  process.stdout.write(JSON.stringify({ ciphertext: encrypted.toString('base64'), digest: createHash('sha256').update(secret).digest('hex'), roundtrip, electron: process.versions.electron }) + '\n')
  app.quit()
}).catch(() => { process.stderr.write('Synthetic encryption probe failed\n'); app.exit(1) })
