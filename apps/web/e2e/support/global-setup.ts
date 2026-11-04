import { startMailServer } from './mail-server'

/** Starts the mail stand-in the Core API sends to (SMTP localhost:1025), and stops it afterwards. */
export default async function globalSetup() {
  const server = await startMailServer()
  return async () => {
    await server.stop()
  }
}
