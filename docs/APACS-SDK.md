# APACS SDK integration

The monitor uses the APACS 3000 SDK only for employee photos. The SQL journal remains the source of access events.

## APACS 7.1 photo chain

The installed APACS SDK exposes the employee photo as:

`TApcCardHolder -> TApcCHMainPhoto -> getCurrentSettings() -> binBufPhoto`

The monitoring application does not read `TAPCCARDHOLDER.FOWNSG` as a photo. `FOWNSG` is APACS security-group data.

## Runtime requirements

- APACS 3000 7.1 must be installed on the machine running the monitor.
- The APACS COM SDK is registered as 32-bit COM, so `ApacsMonitor` is built for x86.
- `APACS 3000 Server` must be available for SDK sessions.
- The default SDK login is `inst` with an empty password, matching the installed APACS C++ SDK sample.
- If the site uses different SDK credentials, set these environment variables for the monitor process:
  - `APACS_SDK_LOGIN`
  - `APACS_SDK_PASSWORD`

## Performance behavior

Photo loading is asynchronous and does not block SQL journal refreshes. Photos are cached by `FSAHOLDER1`/`HolderId`, and only the first visible event range is requested during a refresh. `EventRecord.PhotoBytes` raises `PropertyChanged` when the SDK finishes loading a photo, so the WPF image updates without rebuilding the journal.

If the APACS COM SDK is unavailable, the monitor continues to work from SQL and temporarily suppresses repeated SDK connection attempts.
