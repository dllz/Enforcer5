using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Models
{
    public class ClarifaiOutput
    {
        public Status status { get; set; }
        public Output[] outputs { get; set; }
    }

    public class Status
    {
        public int code { get; set; }
        public string description { get; set; }
    }

    public class Output
    {
        public string id { get; set; }
        public Status1 status { get; set; }
        public DateTime created_at { get; set; }
        public Model model { get; set; }
        public Input input { get; set; }
        public Data1 data { get; set; }
    }

    public class Status1
    {
        public int code { get; set; }
        public string description { get; set; }
    }

    public class Model
    {
        public string name { get; set; }
        public string id { get; set; }
        public DateTime created_at { get; set; }
        public object app_id { get; set; }
        public Output_Info output_info { get; set; }
        public Model_Version model_version { get; set; }
    }

    public class Output_Info
    {
        public string message { get; set; }
        public string type { get; set; }
    }

    public class Model_Version
    {
        public string id { get; set; }
        public DateTime created_at { get; set; }
        public Status2 status { get; set; }
    }

    public class Status2
    {
        public int code { get; set; }
        public string description { get; set; }
    }

    public class Input
    {
        public string id { get; set; }
        public Data data { get; set; }
    }

    public class Data
    {
        public Image image { get; set; }
    }

    public class Image
    {
        public string url { get; set; }
    }

    public class Data1
    {
        public Concept[] concepts { get; set; }
    }

    public class Concept
    {
        public string id { get; set; }
        public string name { get; set; }
        public object app_id { get; set; }
        public float value { get; set; }
    }


    public class ClarifaiInputs
    {
        public InputIn[] inputs { get; set; }
        public ClarifaiInputs(string url)
        {
            inputs = new[]
                 {
                     new InputIn
                     {
                         data = new DataIn()
                         {
                             image = new ImageIn()
                             {
                                  url = url
                             }
                         }
                     }
                 };
        }
    }

    public class InputIn
    {
        public DataIn data { get; set; }
    }

    public class DataIn
    {
        public ImageIn image { get; set; }
    }

    public class ImageIn
    {
        public string url { get; set; }
    }
}
