## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `npm test`
- [ ] `npm run audit:prod`
- [ ] Relevant .NET target frameworks build
- [ ] Write operations return verification and are retry-safe

## Safety and release impact

- [ ] I reviewed transaction, idempotency, batching, and response-size behavior.
- [ ] I updated public documentation and changelog when behavior changed.
- [ ] I inspected package/release contents when packaging changed.
- [ ] This PR contains no credentials, confidential project/model/drawing data,
      internal paths, or unauthorized third-party material.

## Notes for reviewers

Call out compatibility risks, manual Revit verification, or follow-up work.
