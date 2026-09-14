# APACS integration

The monitoring application does not require APACS 3000 to be installed on the workstation.

## Architecture

- Access events are read directly from the APACS SQL database.
- Employee names/cards are joined through `TAPCCARDHOLDERREF` -> `TAPCCARDHOLDER`.
- Employee photos are also requested directly from SQL by `SqlPhotoService`.
- APACS COM/SDK is not used by the monitor.

The old SDK photo chain was:

`TApcCardHolder -> TApcCHMainPhoto -> getCurrentSettings() -> binBufPhoto`

It is documented here only as a reference for understanding the APACS data model. The monitor no longer depends on that chain.

## Photo loading

APACS database revisions can use different physical names for the photo object/blob column. `SqlPhotoService` therefore inspects `INFORMATION_SCHEMA.COLUMNS`, finds the most likely photo/blob table and holder-reference column, and then loads photos in one SQL query for the visible holder IDs.

Photos are cached by `HolderId`. Loading is asynchronous, so the event journal is returned without waiting for image decoding.

If the database revision does not expose a directly joinable photo object, the journal still works normally; the photo loader simply leaves `PhotoBytes` empty.
