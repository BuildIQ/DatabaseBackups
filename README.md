# DatabaseBackups
PGP Encrypted Nightly Backups for SQL Server


This small console application is intended to run in a mirrored (ie. one Witness, one mirror, one primary) server environment for SQL server. It will take a snapshot of the database, compress/encrypt that (allowing access to any number of the specified PGP public keys, generated using OpenPGP) and then move it to the specified directory - which, for us, is an offsite backup location.

It should work fine for a standalone server, there is merely a small check in the middle to prevent redundant backups on the mirrored envrionment. This can be removed/changed as necessary. 

Nb. That it should run on the database server itself (ie. on BOTH the mirror and the primary instances) just in case a failover occurs during the day. You can, of course, run these remotely, but this would mean that the temporary files generated would have to be put on to a mapped SMB/NFS share in order to be encrypted and uploaded off-site.

Any problems/feedback/pull requests are always welcome.

Thanks,

    BuildIQ
