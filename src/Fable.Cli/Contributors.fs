module Fable.Cli.Contributors

let getRandom() =
    let contributors =
        [|
          "zpodlovics";     "zanaptak";           "worldbeater";
          "voronoipotato";  "theimowski";         "tforkmann";
          "stroborobo";     "simra";              "sasmithjr";
          "ritcoder";       "rfrerebe";           "rbauduin";
          "mike-morr";      "kirill-gerasimenko"; "kerams";
          "justinjstark";   "josselinauguste";    "johannesegger";
          "jbeeko";         "iyegoroff";          "intrepion";
          "inchingforward"; "hoonzis";            "goswinr";
          "fbehrens";       "drk-mtr";            "devcrafting";
          "dbrattli";       "damonmcminn";        "ctaggart";
          "cmeeren";        "cboudereau";         "byte-666";
          "bentayloruk";    "SirUppyPancakes";    "Neftedollar";
          "Leonqn";         "Kurren123";          "KevinLamb";
          "BillHally";      "2sComplement";       "xtuc";
          "vbfox";          "selketjah";          "psfblair";
          "pauldorehill";   "mexx";               "matthid";
          "irium";          "halfabench";         "easysoft2k15";
          "dgchurchill";    "Titaye";             "SCullman";
          "MaxWilson";      "JacobChang";         "jmmk";
          "eugene-g";       "ericharding";        "enricosada";
          "cloudRoutine";   "anchann";            "ThisFunctionalTom";
          "0x53A";          "oopbase";            "i-p";
          "battermann";     "Nhowka";             "FrankBro";
          "tomcl";          "piaste";             "fsoikin";
          "scitesy";        "chadunit";           "Pauan";
          "xdaDaveShaw";    "ptrelford";          "johlrich";
          "7sharp9";        "mastoj";             "coolya";
          "valery-vitko";   "Shmew";              "zaaack";
          "markek";         "Alxandr";            "Krzysztof-Cieslak";
          "davidtme";       "nojaf";              "jgrund";
          "tpetricek";      "fdcastel";           "davidpodhola";
          "inosik";         "MangelMaxime";       "Zaid-Ajaj";
          "forki";          "ncave";              "alfonsogarciacaro"
          "do-wa";          "jwosty";             "mlaily";
          "delneg";         "GordonBGood";        "Booksbaum";
          "NickDarvey";     "thinkbeforecoding";  "cartermp";
          "chkn";           "MNie";               "Choc13";
          "davedawkins";    "njlr";               "steveofficer";
          "cannorin";       "thautwarm";          "hensou";
          "IanManske";
        |]
    Array.length contributors
    |> System.Random().Next
    |> fun i -> Array.item i contributors