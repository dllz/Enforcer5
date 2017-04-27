﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;
#pragma warning disable CS0168
#pragma warning disable CS0618
namespace Enforcer5.Handlers
{
    public static class LanguageHelper
    {
        private static List<Language> _langFiles = Program.LangaugeList;
        private static DateTime LastGet = DateTime.MinValue;
        internal static List<Language> GetAllLanguages()
        {
            if (LastGet < DateTime.Now.AddMinutes(60))
            {
                Methods.IntialiseLanguages();
                _langFiles = Program.LangaugeList;
                LastGet = DateTime.Now;
            }
            return _langFiles;
        }

        public static void ValidateFiles(long id, int msgId, string choice = null)
        {
            var errors = new List<LanguageError>();

            //first, let's load up the English file, which is our master file
            var master = XDocument.Load(Path.Combine(Bot.LanguageDirectory, "English.xml"));

            foreach (var langfile in Directory.GetFiles(Bot.LanguageDirectory).Where(x => !x.EndsWith("English.xml")).Select(x => new Language(x)))
                if (langfile.Base == choice || choice == null)
                {
                    //first check the language node
                    CheckLanguageNode(langfile, errors);

                    //test the length
                    TestLength(langfile, errors);

                    //get the file errors
                    GetFileErrors(langfile, errors, master);
                }

            //now pack up the errors and send
            var result = "";
            foreach (var file in errors.Select(x => x.File).Distinct().ToList())
            {
                var langfile = new Language(Path.Combine(Bot.LanguageDirectory, $"{file}.xml"));
                result += $"*{langfile.FileName}.xml* (Last updated: {langfile.LatestUpdate.ToString("MMM dd")})\n";
                if (errors.Any(x => x.Level == ErrorLevel.Info))
                {
                    result += "_Duplicated Strings: _";
                    result = errors.Where(x => x.File == langfile.FileName && x.Level == ErrorLevel.Info).Aggregate(result, (current, fileError) => current + fileError.Key + ", ").TrimEnd(',', ' ') + "\n";
                }
                result += $"_Missing strings:_ {errors.Count(x => x.Level == ErrorLevel.MissingString && x.File == langfile.FileName)}\n";
                if (errors.Any(x => x.File == langfile.FileName && x.Level == ErrorLevel.Error))
                    result = errors.Where(x => x.File == langfile.FileName && x.Level == ErrorLevel.Error).Aggregate(result, (current, fileError) => current + $"_{fileError.Level} - {fileError.Key}_\n{fileError.Message}\n");
                result += "\n";

            }
            Bot.Api.SendTextMessageAsync(id, result, parseMode: ParseMode.Markdown);
            var sortedfiles = Directory.GetFiles(Bot.LanguageDirectory).Select(x => new Language(x)).Where(x => x.Base == (choice ?? x.Base)).OrderBy(x => x.LatestUpdate);
            result = $"*Validation complete*\nErrors: {errors.Count(x => x.Level == ErrorLevel.Error)}\nMissing strings: {errors.Count(x => x.Level == ErrorLevel.MissingString)}";
            result += $"\nMost recently updated file: {sortedfiles.Last().FileName}.xml ({sortedfiles.Last().LatestUpdate.ToString("MMM dd")})\nLeast recently updated file: {sortedfiles.First().FileName}.xml ({sortedfiles.First().LatestUpdate.ToString("MMM dd")})";

            Bot.Api.EditMessageTextAsync(id, msgId, result, parseMode: ParseMode.Markdown);
        }

        public static void ValidateLanguageFile(long id, string filePath, int msgId)
        {
            var errors = new List<LanguageError>();
            var langfile = new Language(filePath);

            //first, let's load up the English file, which is our master file
            var master = XDocument.Load(Path.Combine(Bot.LanguageDirectory, "English.xml"));

            //first check the language node
            CheckLanguageNode(langfile, errors);

            //now test the length
            TestLength(langfile, errors);

            //get the errors
            GetFileErrors(langfile, errors, master);

            //send the result
            var result = $"*{langfile.FileName}.xml* (Last updated: {langfile.LatestUpdate.ToString("MMM dd")})" + Environment.NewLine;
            if (errors.Any(x => x.Level == ErrorLevel.Error))
            {
                result += "_Errors:_\n";
                result = errors.Where(x => x.Level == ErrorLevel.Error).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n{fileError.Message}\n\n");
            }
            if (errors.Any(x => x.Level == ErrorLevel.MissingString))
            {
                result += "_Missing Values:_\n";
                result = errors.Where(x => x.Level == ErrorLevel.MissingString).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n");
            }
            if (errors.Any(x => x.Level == ErrorLevel.Info))
            {
                result += "\n_Duplicated Strings:_\n";
                result = errors.Where(x => x.Level == ErrorLevel.Info).Aggregate(result, (current, fileError) => current + fileError.Key + ", ").TrimEnd(',', ' ');
            }
            result += "\n";
            //Program.Send(result, id);
            Thread.Sleep(500);
            result += $"*Validation complete*.\nErrors: {errors.Count(x => x.Level == ErrorLevel.Error)}\nMissing strings: {errors.Count(x => x.Level == ErrorLevel.MissingString)}";
            Bot.Api.EditMessageTextAsync(id, msgId, result, parseMode: ParseMode.Markdown);

        }

        internal static async void UploadFile(string fileid, long id, string newFileCorrectName, int msgID)
        {
        
            try
            {
            
                var path = Directory.CreateDirectory(Bot.TempLanguageDirectory);
                var newFilePath = Path.Combine(path.FullName, newFileCorrectName);
                using (var fs = new FileStream(newFilePath, FileMode.Create))
                      await Bot.Api.GetFileAsync(fileid, fs);
                //ok, we have the file.  Now we need to determine the language, scan it and the original file.
                var newFileErrors = new List<LanguageError>();
                //first, let's load up the English file, which is our master file
                var langs = Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Select(x => new Language(x));
                var master = XDocument.Load(Path.Combine(Bot.LanguageDirectory, "English.xml"));
                var newFile = new Language(newFilePath);

                //make sure it has a complete langnode
                CheckLanguageNode(newFile, newFileErrors);
    
                //test the length
                TestLength(newFile, newFileErrors);
    
                //check uniqueness
                var error = langs.FirstOrDefault(x =>
                        (x.FileName == newFile.FileName && x.Name != newFile.Name) //check for matching filename and mismatching name
                        || (x.Name == newFile.Name && (x.Base != newFile.Base)) //check for same name and mismatching base-variant
                        || (x.Base == newFile.Base && x.FileName != newFile.FileName) //check for same base-variant and mismatching filename
                                                                                                                      //if we want to have the possibility to rename the file, change previous line with FileName -> Name
                        );
                if (error != null)
                {
                    //problem....
                    newFileErrors.Add(new LanguageError(newFile.FileName, "*Language Node*",
                        $"ERROR: The following file partially matches the same language node. Please check the file name, and the language name, base and variant. Aborting.\n\n*{error.FileName}.xml*\n_Name:_{error.Name}\n_Base:_{error.Base}", ErrorLevel.Error));
                }

                //get the errors in it
                GetFileErrors(newFile, newFileErrors, master);


                //need to get the current file
                var curFile = langs.FirstOrDefault(x => x.Name == newFile.Name);
                var curFileErrors = new List<LanguageError>();

                if (curFile != null)
                {
                    //test the length
                    TestLength(curFile, curFileErrors);

                    ////validate current file name / base / variants match
                    //if (newFile.Base != lang.Base)
                    //{
                    //    newFileErrors.Add(new LanguageError(curFileName, "Language Node", $"Mismatched Base! {newFile.Base} - {lang.Base}", ErrorLevel.Error));
                    //}
                    //if (newFile.Variant != lang.Variant)
                    //{
                    //    newFileErrors.Add(new LanguageError(curFileName, "Language Node", $"Mismatched Variant! {newFile.Variant} - {lang.Variant}", ErrorLevel.Error));
                    //}

                    //get the errors in it
                    GetFileErrors(curFile, curFileErrors, master);
                }

                //send the validation result
                Bot.Api.SendTextMessageAsync(id, OutputResult(newFile, newFileErrors, curFile, curFileErrors), parseMode: ParseMode.Markdown);
                Thread.Sleep(500);


                if (newFileErrors.All(x => x.Level != ErrorLevel.Error))
                {
                    //load up each file and get the names
                    var buttons = new[]
                    {
                    new InlineKeyboardButton($"New", $"upload:{id}:{newFile.FileName}"),
                    new InlineKeyboardButton($"Old", $"upload:{id}:current")
                };
                    var menu = new InlineKeyboardMarkup(buttons.ToArray());
                    Bot.Api.SendTextMessageAsync(id, "Which file do you want to keep?", replyToMessageId: msgID,
                        replyMarkup: menu);
                }
                else
                {
                     Bot.Api.SendTextMessageAsync(id, "Errors present, cannot upload.", replyToMessageId: msgID);
                }
            }
            catch(System.Xml.XmlException XmlExc)
            {
                Bot.Api.SendTextMessageAsync(id, "XML error occured!\n\n" + XmlExc, replyToMessageId: msgID);
            }
            catch(Exception exc)
            {
                Bot.Api.SendTextMessageAsync(id, "Error occured! Exception:\n\n" + exc, replyToMessageId: msgID);
            }
        }



        public static void UseNewLanguageFile(string fileName, long id, int msgId)
        {
            var msg = "Moving file to production..\n";
            msg += "Checking paths for duplicate language file...\n";
            Bot.Api.EditMessageTextAsync(id, msgId, msg);
            fileName += ".xml";
            var tempPath = Bot.TempLanguageDirectory;
            var langPath = Bot.LanguageDirectory;
            var newFilePath = Path.Combine(tempPath, fileName);
            var copyToPath = Path.Combine(langPath, fileName);

            //get the new files language
            var doc = XDocument.Load(newFilePath);

            var newFileLang = new
            {
                Name = doc.Descendants("language").First().Attribute("name").Value,
                Base = doc.Descendants("language").First().Attribute("base").Value,
            };


            //check for existing file
            var langs = Directory.GetFiles(langPath).Select(x => new Language(x)).ToList();
            var lang = langs.FirstOrDefault(x => x.Name == newFileLang.Name && x.FilePath != copyToPath);
            if (lang != null)
            {
                msg += $"Found duplicate language (name attribute) with filename {Path.GetFileNameWithoutExtension(lang.FilePath)}\n";
                copyToPath = lang.FilePath;
            }
            else
            {
                lang = langs.FirstOrDefault(x => x.Base == newFileLang.Base && x.Name != newFileLang.Name);
                if (lang != null)
                {
                    msg += $"Found duplicate language (matching base and variant) with filename {Path.GetFileNameWithoutExtension(lang.FilePath)}\n";
                    msg += "Aborting!";
                    Bot.Api.EditMessageTextAsync(id, msgId, msg);
                    return;
                }
            }


            System.IO.File.Copy(newFilePath, copyToPath, true);
            msg += "File copied to bot\n";
            //#if RELEASE
            //            msg += $"File copied to bot 1\n";
            //#elif RELEASE2
            //            msg += $"File copied to bot 2\n";
            //#endif
            //Bot.Api.EditMessageText(id, msgId, msg);
            //#if RELEASE
            //            copyToPath = copyToPath.Replace("Werewolf 3.0", "Werewolf 3.0 Clone");
            //            System.IO.File.Copy(newFilePath, copyToPath, true);
            //            msg += $"File copied to bot 2\n";
            //            Bot.Api.EditMessageText(id, msgId, msg);
            //#endif
            //var gitPath = Path.Combine(@"C:\Werewolf Source\Werewolf\Werewolf for Telegram\Languages", Path.GetFileName(copyToPath));
            //File.Copy(newFilePath, gitPath, true);
            System.IO.File.Delete(newFilePath);
            msg += $"File copied to git directory\n";
            msg += "* Operation complete.*";

            Bot.Api.EditMessageTextAsync(id, msgId, msg, parseMode: ParseMode.Markdown);
        }

        public static void SendAllFiles(long id)
        {

            //need to zip up all the files
            var path = Path.Combine(Bot.RootDirectory, "languages.zip");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            ZipFile.CreateFromDirectory(Bot.LanguageDirectory, path);
            //now send the file
            var fs = new FileStream(path, FileMode.Open);
            Bot.Api.SendDocumentAsync(id, new FileToSend("languages.zip", fs));
        }

        public static void SendFile(long id, string choice)
        {
            var langOptions = Directory.GetFiles(Bot.LanguageDirectory).Select(x => new Language(x));
            var option = langOptions.First(x => x.Name == choice);
            var fs = new FileStream(option.FilePath, FileMode.Open);
            Bot.Api.SendDocumentAsync(id, new FileToSend(option.FileName + ".xml", fs));
        }

        internal static void SendBase(string choice, long id)
        {
            try
            {
                var zipname = new Regex("[^a-zA-Z0-9]").Replace(choice, "_"); //get rid of non-alphanumeric characters which can cause trouble
                var path = Path.Combine(Bot.LanguageDirectory, $"BaseZips\\{zipname}.zip"); //where the zipfile will be stored
                if (File.Exists(path))
                    File.Delete(path);

                //create our zip file
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var langs = Directory.GetFiles(Bot.LanguageDirectory).Select(x => new Language(x)).Where(x => x.Base == choice); //get the base
                    foreach (var lang in langs)
                        zip.CreateEntryFromFile(Path.Combine(Bot.LanguageDirectory, $"{lang.FileName}.xml"), $"{lang.FileName}.xml", CompressionLevel.Optimal); //add the langs to the zipfile
                }
                //now send the zip file
                var fs = new FileStream(path, FileMode.Open);
                Bot.Api.SendDocumentAsync(id, new FileToSend($"{zipname}.zip", fs));

                //uncomment following line if you don't want to store those zipfiles
                //File.Delete(path);
            }
            catch (Exception e)
            {
                Bot.Api.SendTextMessageAsync(id, e.Message);
            }
        }

        #region Helpers

        private static string GetLocaleString(string key, XDocument file)
        {
            var strings = file.Descendants("string").FirstOrDefault(x => x.Attribute("key").Value == key);
            var values = strings.Descendants("value");
            return values.First().Value;
        }

        private static void CheckLanguageNode(Language langfile, List<LanguageError> errors)
        {
            if (langfile.Name == null)
                errors.Add(new LanguageError(langfile.FileName, "*Language Node*", "Language name is missing", ErrorLevel.Error));
            if (langfile.Base == null)
                errors.Add(new LanguageError(langfile.FileName, "*Language Node*", "Base is missing", ErrorLevel.Error));
        }

        private static string OutputResult(Language newFile, List<LanguageError> newFileErrors, Language curFile, List<LanguageError> curFileErrors)
        {
            var result = $"NEW FILE\n*{newFile.FileName}.xml - ({newFile.Name ?? ""})*" + Environment.NewLine;
            if (newFileErrors.Any(x => x.Level == ErrorLevel.Error))
            {
                result += "_Errors:_\n";
                result = newFileErrors.Where(x => x.Level == ErrorLevel.Error).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n{fileError.Message}\n\n");
            }
            if (newFileErrors.Any(x => x.Level == ErrorLevel.MissingString))
            {
                result += "_Missing Values:_\n";
                result = newFileErrors.Where(x => x.Level == ErrorLevel.MissingString).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n");
            }
            if (newFileErrors.Any(x => x.Level == ErrorLevel.Info))
            {
                result += "\n_Warning:_\n";
                result = newFileErrors.Where(x => x.Level == ErrorLevel.Info).Aggregate(result, (current, fileError) => current + $"{fileError.Message}\n");
                //next line is there because ErrorLevel.Info is used only to check for duplicated strings. if we use ErrorLevel.Info for other things, this probably should be changed.
                result += "The second instance of the string won't be used, unless you move one of the two values inside the other. Check the latest English file to see how this is fixed.\n\n";
            }
            if (newFileErrors.Count == 0)
            {
                result += "_No errors_\n";
            }
            if (curFile != null)
            {
                result += "\n\n";
                result += $"OLD FILE (Last updated: {curFile.LatestUpdate.ToString("MMM dd")})\n*{curFile.FileName}.xml - ({curFile.Name})*\n";
                result +=
                    $"Errors: {curFileErrors.Count(x => x.Level == ErrorLevel.Error)}\nMissing strings: {curFileErrors.Count(x => x.Level == ErrorLevel.MissingString)}";
            }
            else
            {
                result += "\n\n*No old file, this is a new language*";
                result += "\nPlease double check the filename, and the language name, base and variant, as you won't be able to change them.";
                result += $"\n_Name:_ {newFile.Name ?? ""}";
                result += $"\n_Base:_ {newFile.Base ?? ""}";
                if (Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Select(x => new Language(x)).All(x => x.Base != newFile.Base))
                    result += " *(NEW)*";
            }

            return result;
        }

        private static void TestLength(Language file, List<LanguageError> fileErrors)
        {
            var test = $"setlang|-1001049529775|{file.Base ?? ""}|v";
            var count = Encoding.UTF8.GetByteCount(test);
            if (count > 64)
                fileErrors.Add(new LanguageError(file.FileName, "*Language Node*", "Base and variant are too long. (*38 utf8 byte max*)", ErrorLevel.Error));
        }

        private static void GetFileErrors(Language file, List<LanguageError> fileErrors, XDocument master)
        {
            var masterStrings = master.Descendants("string");

            //check for CultConvertSerialKiller & CupidChosen duplication
            var dup = file.Doc.Descendants("string").Count(x => x.Attribute("key").Value == "CultConvertSerialKiller");
            if (dup > 1)
                fileErrors.Add(new LanguageError(file.FileName, "CultConvertSerialKiller", "CultConvertSerialKiller duplication", ErrorLevel.Info));
            dup = file.Doc.Descendants("string").Count(x => x.Attribute("key").Value == "CupidChosen");
            if (dup > 1)
                fileErrors.Add(new LanguageError(file.FileName, "CupidChosen", "CupidChosen duplication", ErrorLevel.Info));

            foreach (var str in masterStrings)
            {
                var key = str.Attribute("key").Value;
                var isgif = str.Attributes().Any(x => x.Name == "isgif");
                var deprecated = str.Attributes().Any(x => x.Name == "deprecated");
                //get the english string
                //get the locale values
                var masterString = GetLocaleString(key, master);
                var values = file.Doc.Descendants("string")
                        .FirstOrDefault(x => x.Attribute("key").Value == key)?
                        .Descendants("value");
                if (values == null)
                {
                    if (!deprecated)
                        fileErrors.Add(new LanguageError(file.FileName, key, $"Values missing"));
                    continue;
                }
                //check master string for {#} values
                int vars = 0;
                if (masterString.Contains("{0}"))
                    vars = 1;
                if (masterString.Contains("{1}"))
                    vars = 2;
                if (masterString.Contains("{2}"))
                    vars = 3;
                if (masterString.Contains("{3}"))
                    vars = 4;
                if (masterString.Contains("{4}"))
                    vars = 5;

                foreach (var value in values)
                {
                    for (int i = 0; i <= 5 - 1; i++)
                    {
                        if (!value.Value.Contains("{" + i + "}") && vars - 1 >= i)
                        {
                            //missing a value....
                            fileErrors.Add(new LanguageError(file.FileName, key, "Missing {" + i + "}", ErrorLevel.Error));
                        }
                        else if (value.Value.Contains("{" + i + "}") && vars - 1 < i)
                        {
                            fileErrors.Add(new LanguageError(file.FileName, key, "Extra {" + i + "}", ErrorLevel.Error));
                        }
                    }

                    if (isgif && value.Value.Length > 200)
                    {
                        fileErrors.Add(new LanguageError(file.FileName, key, "GIF string length cannot exceed 200 characters", ErrorLevel.Error));
                    }
                }
            }
        }


        #endregion
    }

    public class LanguageError
    {
        public string File { get; set; }
        public string Key { get; set; }
        public string Message { get; set; }
        public ErrorLevel Level { get; set; }

        public LanguageError(string file, string key, string message, ErrorLevel level = ErrorLevel.MissingString)
        {
            File = file;
            Key = key;
            Message = message;
            Level = level;
        }
    }

    public enum ErrorLevel
    {
        Info, MissingString, Error
    }
}
