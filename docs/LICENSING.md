# Licensing summary

Plain-language summary for the people who decide how this fork is used and
shared. **It is not legal advice.** When a decision depends on it, ask
counsel, and read the license texts themselves.

## What is in this repository and a release package

| Component | License | Where the text is |
|---|---|---|
| Service wrapper (`RedisService.exe`), CI workflows, scripts, docs, configs | Apache License 2.0 (from upstream redis-windows) | `LICENSE` |
| Redis 8.x server and tools (`redis-server.exe`, `redis-cli.exe`, ...), including the bundled vector-sets module | Your choice of **RSALv2**, **SSPLv1** or **AGPLv3** | `LICENSE.txt` in the Redis source |
| This fork's Redis patches (`patches/redis/*.patch`) | Modifications of Redis: treat them as covered by the Redis licenses above, not by Apache 2.0 | - |
| hiredis (bundled in Redis) | BSD 3-clause | `deps/hiredis/COPYING` |
| Lua (bundled in Redis) | MIT | `deps/lua/COPYRIGHT` |
| Cygwin / MSYS2 runtime (`cygwin1.dll`, `msys-2.0.dll`) | LGPLv3 (to be confirmed against the runtime package's own license files) | runtime package |
| OpenSSL 3 (`*crypto-3.dll`, `*ssl-3.dll`, TLS builds only) | Apache License 2.0 | OpenSSL package |
| GCC runtime (`*gcc_s-seh-1.dll`, `*stdc++-6.dll`) | GPLv3 with the GCC Runtime Library Exception | GCC package |
| Other Cygwin DLLs (zlib, iconv, intl) | zlib / LGPL | respective packages |

Redis 7.2 and earlier were BSD-licensed; this fork builds Redis 8.x only, so
the tri-license applies.

## Using the fork internally

Running a privately modified Redis build for your own application, on servers
your organisation controls, is allowed under all three Redis licenses:

- **RSALv2** restricts offering Redis to third parties as a managed service,
  and removing or obscuring license notices. Internal use by your own
  application is neither.
- **SSPLv1** obligations start when you offer the software itself as a service
  to others.
- **AGPLv3** obligations start when you distribute the software, or when third
  parties interact with a modified version over a network. A cache used only
  by your own application, not exposed to outside users, does not do that.

Keeping the modified source in a private repository is fine in this case.

## The distribution caveat - decide before the fork spreads

**Installing the fork on a server that a customer owns or controls is
distribution, not internal use** - even if your own staff do the install and
the customer never touches Redis directly. That applies to per-customer
rollouts on customer-hosted servers. Before the binaries leave servers your
organisation controls, pick the license you distribute under and meet its
terms:

- Under **AGPLv3**: give each recipient the complete corresponding source of
  the binaries you gave them (Redis source at the tag, plus this repository's
  patch series and build scripts), under AGPLv3, and keep the license notices.
- Under **RSALv2**: distribution is allowed, but the recipient receives the
  RSALv2 restrictions (no managed-service offering), and all notices must
  stay intact.
- **SSPLv1** is rarely the practical choice for distribution.

The bundled runtime DLLs travel with the package and keep their own terms:
ship their license texts with the package, and for the LGPL runtime be ready
to provide its source (the MSYS2/Cygwin source package of the exact version
recorded in the build's `build-info` file). OpenSSL is Apache 2.0; a no-TLS
build does not ship it at all.

## Publishing changes later

- **Wrapper, CI, scripts, docs** can be published under Apache 2.0.
- **Redis patches** published as open source would have to be under one of
  the Redis licenses; AGPLv3 is the only OSI-approved choice among them.
- **Contributing upstream** to redis/redis is governed by its own contributor
  agreement (see `CONTRIBUTING.md` in the Redis source); read it first.

## Hosting the private repository

GitHub does not allow a *private* fork of a public repository. The private
copy is therefore a separate repository created by mirror-pushing upstream,
with upstream kept as a git remote for merges. Keep the upstream `LICENSE`
file in place (the upstream repository has no `NOTICE` file today; if one
appears, keep it too), and keep the "not affiliated with Redis Ltd."
disclaimer in the README.
