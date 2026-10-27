using System;
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
using Enforcer5.LanguageUpload;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;
using Telegram.Bot;
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

            foreach (var langfile in Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Where(x => !x.EndsWith("English.xml")).Select(x => new Language(x)))
                if (langfile.Base == choice || choice == null)
                {
                    errors.AddRange(LanguageValidator.Check(langfile, master));
                }

            //now pack up the errors and send
            var result = "";
            foreach (var file in errors.Select(x => x.File).Distinct().ToList())
            {
                var langfile = new Language(Path.Combine(Bot.LanguageDirectory, $"{file}.xml"));
                result += $"*{langfile.FileName}.xml* (Last updated: {langfile.LatestUpdate.ToString("MMM dd")})\n";
                if (errors.Any(x => x.Level == ErrorLevel.DuplicatedString))
                {
                    result += "_Duplicated Strings: _";
                    result = errors.Where(x => x.File == langfile.FileName && x.Level == ErrorLevel.DuplicatedString).Aggregate(result, (current, fileError) => current + fileError.Key + ", ").TrimEnd(',', ' ') + "\n";
                }
                result += $"_Missing strings:_ {errors.Count(x => x.Level == ErrorLevel.MissingString && x.File == langfile.FileName)}\n";
                if (errors.Any(x => x.File == langfile.FileName && IsProblem(x)))
                    result = errors.Where(x => x.File == langfile.FileName && IsProblem(x)).Aggregate(result, (current, fileError) => current + $"_{fileError.Level} - {fileError.Key}_\n{fileError.Message}\n");
                result += "\n";

            }
            Bot.Api.SendMessage(id, result, parseMode: ParseMode.Markdown);
            var sortedfiles = Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Select(x => new Language(x)).Where(x => x.Base == (choice ?? x.Base)).OrderBy(x => x.LatestUpdate);
            result = $"*Validation complete*\nErrors: {errors.Count(x => IsProblem(x))}\nMissing strings: {errors.Count(x => x.Level == ErrorLevel.MissingString)}";
            result += $"\nMost recently updated file: {sortedfiles.Last().FileName}.xml ({sortedfiles.Last().LatestUpdate.ToString("MMM dd")})\nLeast recently updated file: {sortedfiles.First().FileName}.xml ({sortedfiles.First().LatestUpdate.ToString("MMM dd")})";

            Bot.Api.EditMessageText(id, msgId, result, parseMode: ParseMode.Markdown);
        }

        public static void ValidateLanguageFile(long id, string filePath, int msgId)
        {
            var errors = new List<LanguageError>();
            var langfile = new Language(filePath);

            //first, let's load up the English file, which is our master file
            var master = XDocument.Load(Path.Combine(Bot.LanguageDirectory, "English.xml"));

            errors.AddRange(LanguageValidator.Check(langfile, master));

            //send the result
            var result = $"*{langfile.FileName}.xml* (Last updated: {langfile.LatestUpdate.ToString("MMM dd")})" + Environment.NewLine;
            if (errors.Any(x => IsProblem(x)))
            {
                result += "_Errors:_\n";
                result = errors.Where(x => IsProblem(x)).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n{fileError.Message}\n\n");
            }
            if (errors.Any(x => x.Level == ErrorLevel.MissingString))
            {
                result += "_Missing Values:_\n";
                result = errors.Where(x => x.Level == ErrorLevel.MissingString).Aggregate(result, (current, fileError) => current + $"{fileError.Key}\n");
            }
            if (errors.Any(x => x.Level == ErrorLevel.DuplicatedString))
            {
                result += "\n_Duplicated Strings:_\n";
                result = errors.Where(x => x.Level == ErrorLevel.DuplicatedString).Aggregate(result, (current, fileError) => current + fileError.Key + ", ").TrimEnd(',', ' ');
            }
            result += "\n";
            //Program.Send(result, id);
            Thread.Sleep(500);
            result += $"*Validation complete*.\nErrors: {errors.Count(x => IsProblem(x))}\nMissing strings: {errors.Count(x => x.Level == ErrorLevel.MissingString)}";
            Bot.Api.EditMessageText(id, msgId, result, parseMode: ParseMode.Markdown);

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
            Bot.Api.SendDocument(id, InputFile.FromStream(fs, "languages.zip"));
        }

        public static void SendFile(long id, string choice)
        {
            var langOptions = Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Select(x => new Language(x));
            var option = langOptions.First(x => x.Name == choice);
            var fs = new FileStream(option.FilePath, FileMode.Open);
            Bot.Api.SendDocument(id, InputFile.FromStream(fs, option.FileName + ".xml"));
        }

        internal static void SendBase(string choice, long id)
        {
            try
            {
                var zipname = new Regex("[^a-zA-Z0-9]").Replace(choice, "_"); //get rid of non-alphanumeric characters which can cause trouble
                var tempDirectory = Directory.CreateDirectory(Bot.TempLanguageDirectory);
                var path = Path.Combine(tempDirectory.FullName, $"{zipname}-{Guid.NewGuid():N}.zip"); //where the zipfile will be stored
                if (File.Exists(path))
                    File.Delete(path);

                //create our zip file
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var langs = Directory.GetFiles(Bot.LanguageDirectory, "*.xml").Select(x => new Language(x)).Where(x => x.Base == choice); //get the base
                    foreach (var lang in langs)
                        zip.CreateEntryFromFile(Path.Combine(Bot.LanguageDirectory, $"{lang.FileName}.xml"), $"{lang.FileName}.xml", CompressionLevel.Optimal); //add the langs to the zipfile
                }
                //now send the zip file
                var fs = new FileStream(path, FileMode.Open);
                Bot.Api.SendDocument(id, InputFile.FromStream(fs, $"{zipname}.zip"));

                //uncomment following line if you don't want to store those zipfiles
                //File.Delete(path);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Sending language base {choice}");
                Bot.Api.SendMessage(id, e.Message);
            }
        }

        #region Helpers

        /// <summary>Anything worse than a missing or doubled string: what /validatelanguages lists as an error.</summary>
        private static bool IsProblem(LanguageError e) =>
            e.Level is ErrorLevel.Error or ErrorLevel.GifTooLong or ErrorLevel.FatalError;

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
        /// <summary>A key in the file twice (a warning in uploads).</summary>
        DuplicatedString,
        /// <summary>A string English has and the file does not; English is shown. Never blocks.</summary>
        MissingString,
        /// <summary>A placeholder ({0}) missing or extra (a warning in uploads).</summary>
        Error,
        /// <summary>A GIF's text over 200 characters (a warning in uploads).</summary>
        GifTooLong,
        /// <summary>The file cannot be used at all: language node, base length, a clash, unreadable.</summary>
        FatalError
    }
}
