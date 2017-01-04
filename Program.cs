/* MIT License

Copyright(c) 2016 BuildIQ LIMITED, www.buildiq.co.uk

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/

using System;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;

namespace DatabaseBackup
{
    class backup
    {
#if DEBUG

        /// <summary>
        /// An array of PGP Public Key files which have the ability to decrypt the database backups (nb. these should be placed on the same server/s (if load balanced) that the database is running on
        /// </summary>
        private static string[] PGP_PUBLIC_KEYS_FILEPATHS = { "C:\\temp\\1.asc", "C:\\temp\\2.asc" };

        /// <summary>
        /// The mapped drive/folder to save files to
        /// </summary>
        private const string offsite_Drive_Location = "c:\\temp";

        /// <summary>
        /// The location to save the temporary original (unencrypted) backup file to. 
        /// </summary>
        private const string onsite_Drive_Location = "c:\\temp";

        /// <summary>
        /// name of the database to backup to
        /// </summary>
        private const string database_Name = "YourDBName";

        /// <summary>
        /// Delete any archived backups which are older than x days.
        /// </summary>
        private const int deleteOlderThanDays = 365;

        /// <summary>
        /// Database connection string
        /// </summary>
        private const string connectionString = "changme";

#endif

        /// <summary>
        /// Called every day at midnight using Windows Task Scheduler. 
        /// Backup the current database to a file on the SQL server -> PGP encrypt the file -> upload to offsite mapped location -> delete the original file -> delete any copies older than x days 
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            try
            {
                var now = DateTime.Now;
                var sNow = DateTime.Now.ToString("MM-dd-yyyy");

                var unencryptedFilePath = onsite_Drive_Location + "\\" + sNow + ".bak";

                var encryptedFilePath = unencryptedFilePath + ".encrypted";

                // We are running in a mirrored environment, with this executing on both nodes as scheduled tasks. Make sure that the active node hostname matches our hostname, as there is no point backing up twice, or executing the statement on a node which is not the primary instance.  
                var myHostname = Environment.GetEnvironmentVariable("COMPUTERNAME");                 

                // Backup the database 
                using (SqlConnection connection = new SqlConnection(
             connectionString))
                {

                    var hostnameCommand = new SqlCommand("select SERVERPROPERTY('ComputerNamePhysicalNetBIOS')", connection);

                    hostnameCommand.Connection.Open();

                    var sqlServerHostnameReader = hostnameCommand.ExecuteReader();

                    if (sqlServerHostnameReader.HasRows)
                    {
                        while (sqlServerHostnameReader.Read())
                        {
                            var sqlServerHostname = sqlServerHostnameReader.GetString(0);

                            Console.WriteLine("Active SQL Server instance is " + sqlServerHostname);

                            Console.WriteLine("My hostname is " + myHostname);

                            if (sqlServerHostname != myHostname)
                            {
                                Console.WriteLine("Invalid hostname, bailing");

                                return;
                            }
                        }
                    }
                    else
                    {
                        return;
                    }
                    
                    hostnameCommand.Connection.Close();

                    var command = new SqlCommand(
                        @"USE " + database_Name + @";
                            BACKUP DATABASE " + database_Name +
                             @" TO DISK = '" + unencryptedFilePath + @"';", connection);
                    command.Connection.Open();
                    command.ExecuteNonQuery();
                }

                // Read/encrypt the local file
                EncryptPGPFile(unencryptedFilePath, encryptedFilePath);

                // if we've already taken one for today, delete it and replace it
                if (File.Exists(offsite_Drive_Location + "\\" + Path.GetFileName(encryptedFilePath)))
                {
                    File.Delete(offsite_Drive_Location + "\\" + Path.GetFileName(encryptedFilePath));
                }

                // Copy to offsite location
                File.Move(encryptedFilePath, offsite_Drive_Location + "\\" + Path.GetFileName(encryptedFilePath));

                // Delete the unencrypted backup
                File.Delete(unencryptedFilePath);

                // Delete any offsite backups older than x days
                var date = DateTime.Now.AddDays(-deleteOlderThanDays);

                var oldFileToDeletePath = offsite_Drive_Location + "\\" + date.ToString("MM-dd-yyyy") + ".bak.encrypted";

                int i = 0;

                // while any files exist which are older than the specified date, delete them.
                while (File.Exists(oldFileToDeletePath))
                {
                    try
                    {
                        File.Delete(oldFileToDeletePath);
                        i++;
                        oldFileToDeletePath = offsite_Drive_Location + "\\" + date.AddDays(i + deleteOlderThanDays).ToString("MM-dd-yyyy") + ".bak.encrypted";
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine("Failed to delete file " + date.ToString("MM-dd-yyyy") + " " + ex);
                    }
                }

                Console.WriteLine("Complete");           
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error - " + ex);
            }
        }

        /// <summary>
        /// https://gist.github.com/dieseltravis/8323431
        /// </summary>
        /// <param name="inputFile"></param>
        /// <param name="outputFile"></param>
        private static void EncryptPGPFile(string inputFile, string outputFile)
        {
            using (MemoryStream outputBytes = new MemoryStream())
            {
                PgpCompressedDataGenerator dataCompressor = new PgpCompressedDataGenerator(CompressionAlgorithmTag.Zip);
                PgpUtilities.WriteFileToLiteralData(dataCompressor.Open(outputBytes), PgpLiteralData.Binary, new FileInfo(inputFile));

                dataCompressor.Close();
                PgpEncryptedDataGenerator dataGenerator = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Cast5, true, new SecureRandom());
                foreach (var key in PGP_PUBLIC_KEYS_FILEPATHS)
                {
                    using (Stream publicKeyStream = File.OpenRead(key))
                    {

                        PgpPublicKey pubKey = ImportPublicKey(publicKeyStream);

                        dataGenerator.AddMethod(pubKey);
                    }
                }

                byte[] dataBytes = outputBytes.ToArray();

                using (Stream outputStream = File.Create(outputFile))
                {
                    using (ArmoredOutputStream armoredStream = new ArmoredOutputStream(outputStream))
                    {
                        using (Stream oStream = dataGenerator.Open(armoredStream, dataBytes.Length))
                        {
                            oStream.Write(dataBytes, 0, dataBytes.Length);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// stackoverflow.com/questions/18856937/openpgp-encryption-with-bouncycastle
        /// </summary>
        /// <param name="publicIn"></param>
        /// <returns></returns>

        public static PgpPublicKey ImportPublicKey(Stream publicIn)
        {
            var pubRings =
                new PgpPublicKeyRingBundle(PgpUtilities.GetDecoderStream(publicIn)).GetKeyRings().OfType<PgpPublicKeyRing>();
            var pubKeys = pubRings.SelectMany(x => x.GetPublicKeys().OfType<PgpPublicKey>());
            var pubKey = pubKeys.FirstOrDefault();
            return pubKey;
        }

    }
}
