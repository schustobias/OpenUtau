using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OpenUtau.Api;
using OpenUtau.Core.G2p;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("German VCCV Phonemizer", "DE VCCV", "Lotte V", language: "DE")]
    public class GermanVCCVPhonemizer : SyllableBasedPhonemizer {
        /// <summary>
        /// German VCCV phonemizer.
        /// Based on UTAU Felix's VCCV voicebank.
        /// Pronunciation reference: https://docs.google.com/spreadsheets/d/12E62ImRDOXyS6g6BFJHT9pOU2cFmNrch1UEmuTZqeak/edit?pli=1#gid=0
        /// </summary>
        /// 

        private readonly string[] vowels = "a,6,e,E,2,i,I,y,Y,u,U,o,O,@,aU,OY,aI".Split(',');
        private readonly string[] consonants = "-,b,C,d,f,g,h,j,k,kh,l,m,n,N,p,ph,R;,s,S,t,th,v,x,z,Z,dZ,ks,pf,st,St,tS,w".Split(',');
        private readonly string[] longConsonants = "k,kh,p,ph,s,S,t,th,dZ,ks,pf,st,St,tS".Split(',');
        private readonly Dictionary<string, string> dictionaryReplacements = ("aa=a,ae=E,ah=@,ao=O,aw=aU,ax=@,ay=aI," +
            "b=b,cc=C,ch=tS,d=d,dh=z," + "ee=e,eh=E,er=6,ex=6," + "f=f,g=g,hh=h,ih=I,iy=i,jh=dZ,k=k,l=l,m=m,n=n,ng=N," +
            "oe=9,ohh=2,ooh=o,oy=OY," + "p=p,pf=pf,q=-,r=R;,rr=R;,s=s,sh=S,t=t," + "th=s,ts=ts," + "ue=y,uh=U,uw=u," + "v=v,w=w,x=x,y=j," +
            "yy=Y," + "z=z,zh=Z").Split(',')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        protected override string[] GetVowels() => vowels;
        protected override string[] GetConsonants() => consonants;
        protected override string GetDictionaryName() => "cmudict_de.txt";
        protected override Dictionary<string, string> GetDictionaryPhonemesReplacement() => dictionaryReplacements;

        protected override IG2p LoadBaseDictionary() {
            var g2ps = new List<IG2p>();

            // Load dictionary from plugin folder.
            string path = Path.Combine(PluginDir, "de_vccv.yaml");
            if (!File.Exists(path)) {
                Directory.CreateDirectory(PluginDir);
                File.WriteAllBytes(path, Data.Resources.de_vccv_template);
            }
            g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(path)).Build());

            // Load dictionary from singer folder.
            if (singer != null && singer.Found && singer.Loaded) {
                string file = Path.Combine(singer.Location, "de_vccv.yaml");
                if (File.Exists(file)) {
                    try {
                        g2ps.Add(G2pDictionary.NewBuilder().Load(File.ReadAllText(file)).Build());
                    } catch (Exception e) {
                        Log.Error(e, $"Failed to load {file}");
                    }
                }
            }

            // Load base g2p.
            g2ps.Add(new GermanG2p());

            return new G2pFallbacks(g2ps.ToArray());
        }

        protected override string[] GetSymbols(Note note) {
            string[] original = base.GetSymbols(note);
            if (original == null) {
                return null;
            }
            List<string> modified = new List<string>();
            string[] diphthongs = new[] { "aU", "OY", "aI" };
            foreach (string s in original) {
                if (diphthongs.Contains(s)) {
                    modified.AddRange(new string[] { s[0].ToString(), s[1].ToString() });
                } else {
                    modified.Add(s);
                }
            }
            return modified.ToArray();
        }

        protected override List<string> ProcessSyllable(Syllable syllable) {
            string prevV = syllable.prevV;
            string[] cc = syllable.cc;
            string v = syllable.v;

            string basePhoneme;
            var phonemes = new List<string>();
            var lastC = cc.Length - 1;
            var firstC = 0;

            if (syllable.IsStartingV) {
                basePhoneme = $"- {v}"; ;
            } else if (syllable.IsVV) {
                var vv = $"{prevV} {v}";
                if (!CanMakeAliasExtension(syllable)) {
                    if (HasOto(vv, syllable.vowelTone)) {
                        basePhoneme = vv;
                    } else if (!HasOto(vv, syllable.vowelTone) && HasOto(ValidateAlias(vv), syllable.vowelTone)) {
                        basePhoneme = ValidateAlias(vv);
                    } else {
                        basePhoneme = $"-{v}";
                        phonemes.Add($"{prevV} -");
                    }
                } else {
                    // the previous alias will be extended
                    basePhoneme = null;
                }
            } else if (syllable.IsStartingCVWithOneConsonant) {
                var rcv = $"- {cc[0]}{v}";
                if (HasOto(rcv, syllable.vowelTone)) {
                    basePhoneme = rcv;
                } else {
                    basePhoneme = $"{cc[0]} {v}";
                }
            } else if (syllable.IsStartingCVWithMoreThanOneConsonant) {
                var rccv = $"- {string.Join("", cc)}{v}";
                if (HasOto(rccv, syllable.vowelTone)) {
                    basePhoneme = rccv;
                } else {
                    basePhoneme = $"{cc.Last()}{v}";
                    // try RCC, with or without schwa
                    for (var i = cc.Length; i > 1; i--) {
                        if (TryAddPhoneme(phonemes, syllable.tone, $"- {string.Join("", cc.Take(i))}", $"- {string.Join("", cc.Take(i))}@")) {
                            firstC = i - 1;
                            break;
                        }
                    }
                    if (phonemes.Count == 0) {
                        // try RC with schwa if no RCC
                        TryAddPhoneme(phonemes, syllable.tone, $"- {cc[0]}@");
                    }
                }
            } else {
                basePhoneme = $"{cc.Last()}{v}";
                // try CCV
                if (cc.Length - firstC > 1) {
                    for (var i = firstC; i < cc.Length; i++) {
                        var ccv = $"{string.Join("", cc.Skip(i))}{v}";
                        if (HasOto(ccv, syllable.vowelTone)) {
                            lastC = i;
                            basePhoneme = ccv;
                            break;
                        } else if (!HasOto(ccv, syllable.vowelTone) && HasOto(ValidateAlias(ccv), syllable.vowelTone)) {
                            lastC = i;
                            basePhoneme = ValidateAlias(ccv);
                            break;
                        }
                    }
                }
                // try vcc
                for (var i = lastC + 1; i >= 0; i--) {
                    var vcc = $"{prevV} {string.Join("", cc.Take(3))}";
                    var vcc2 = $"{prevV}{string.Join("", cc.Take(2))}";
                    var vc = $"{prevV} {cc[0]}";
                    var vc2 = $"{prevV}{cc[0]}";
                    if (i == 0) {
                        phonemes.Add(vc);
                    } else if (HasOto(vcc, syllable.tone)) {
                        phonemes.Add(vcc);
                        break;
                    } else if (!HasOto(vcc, syllable.tone) && HasOto(vcc2, syllable.tone)) {
                        phonemes.Add(vcc2);
                        break;
                    } else if (!HasOto(vcc2, syllable.tone) && HasOto(vc2, syllable.tone)) {
                        phonemes.Add(vc2);
                        break;
                    } else if (!HasOto(vc2, syllable.tone) && HasOto(vc, syllable.tone)) {
                        phonemes.Add(vc);
                        break;
                    } else {
                        continue;
                    }
                }
            }
            for (var i = firstC; i < lastC; i++) {
                // we could use some CCV, so lastC is used
                // we could use -CC so firstC is used
                var rccv = $"- {string.Join("", cc)}{v}";
                var cc1 = $"{string.Join("", cc.Skip(i))}";
                var ccv = string.Join("", cc.Skip(i)) + v;
                if (!HasOto(rccv, syllable.vowelTone)) {
                    if (!HasOto(cc1, syllable.tone)) {
                        // joined CC
                        cc1 = ValidateAlias(cc1);
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        // [C1][C2] -
                        cc1 = $"{cc[i]}{cc[i + 1]} -";
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        cc1 = ValidateAlias(cc1);
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        // [C1][C2]
                        cc1 = $"{cc[i]}{cc[i + 1]}";
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        cc1 = ValidateAlias(cc1);
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        // [C1] [C2]
                        cc1 = $"{cc[i]} {cc[i + 1]}";
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        cc1 = ValidateAlias(cc1);
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        // use schwa when no CC
                        cc1 = $"{cc[i]}{cc[i + 1]}@";
                    }
                    if (!HasOto(cc1, syllable.tone)) {
                        cc1 = ValidateAlias(cc1);
                    }
                    if (HasOto(ccv, syllable.vowelTone)) {
                        basePhoneme = ccv;
                    }
                    if (i + 1 < lastC) {
                        var cc2 = $"{string.Join("", cc.Skip(i))}";
                        if (!HasOto(cc2, syllable.tone)) {
                            // joined CC
                            cc2 = ValidateAlias(cc2);
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            // [C2][C3] -
                            cc2 = $"{cc[i + 1]}{cc[i + 2]} -";
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            // [C2][C3]
                            cc2 = $"{cc[i + 1]}{cc[i + 2]}";
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            // [C2] [C3]
                            cc2 = $"{cc[i + 1]} {cc[i + 2]}";
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            // use schwa when no CC
                            cc2 = $"{cc[i + 1]}{cc[i + 2]}@";
                        }
                        if (!HasOto(cc2, syllable.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (HasOto(ccv, syllable.vowelTone)) {
                            basePhoneme = ccv;
                        }
                        if (TryAddPhoneme(phonemes, syllable.tone, $"{cc[i]}{cc[i + 1]}{cc[i + 2]} -")) {
                            // if it exists, use [C1][C2][C3] -
                            i++;
                        } else if (HasOto(cc1, syllable.tone) && HasOto(cc2, syllable.tone)) {
                            // like [V C1] [C1 C2] [C2 C3] [C3 ..]
                            phonemes.Add(cc1);
                        } else if (TryAddPhoneme(phonemes, syllable.tone, cc1)) {
                        //    // like [V C1] [C1 C2] [C2 ..]
                        }
                    } else {
                        TryAddPhoneme(phonemes, syllable.tone, cc1);
                    }
                }
            }
            phonemes.Add(basePhoneme);
            return phonemes;
        }

        protected override List<string> ProcessEnding(Ending ending) {
            string[] cc = ending.cc;
            string v = ending.prevV;

            var phonemes = new List<string>();
            if (ending.IsEndingV) {
                var vr = $"{v}-";
                if (HasOto(vr, ending.tone)) {
                    phonemes.Add(vr);
                } else {
                    phonemes.Add($"{v} -");
                }
            } else if (ending.IsEndingVCWithOneConsonant) {
                phonemes.Add($"{v}{cc[0]}");
            } else {
                var vcc = $"{v}{string.Join("", cc)}";
                if (HasOto(vcc, ending.tone)) {
                    phonemes.Add(vcc);
                } else {
                    phonemes.Add($"{v}{cc[0]}");
                    for (var i = 0; i < cc.Length - 1; i++) {
                        var ccr = $"{cc[i]}{cc[i + 1]} -";
                        if (!HasOto(ccr, ending.tone)) {
                            // in most cases, use [C1][C2] -
                            ccr = ValidateAlias(ccr);
                        }
                        if (!HasOto(ccr, ending.tone)) {
                            // use schwa when no CCR
                            ccr = $"{cc[i]}{cc[i + 1]}@";
                        }
                        if (!HasOto(ccr, ending.tone)) {
                            ccr = ValidateAlias(ccr);
                        }
                        if (i < cc.Length - 2) {
                            var ccr2 = $"{cc[i]}{cc[i + 1]}{cc[i + 2]} -";
                            if (HasOto(ccr2, ending.tone)) {
                                // if it exists, use [C1][C2][C3] -
                                phonemes.Add(ccr2);
                                i++;
                            } else if (!HasOto(ccr2, ending.tone) && HasOto(ValidateAlias(ccr2), ending.tone)) {
                                phonemes.Add(ValidateAlias(ccr2));
                                i++;
                            }
                            else {
                                TryAddPhoneme(phonemes, ending.tone, ccr, ValidateAlias(ccr));
                            }
                        } else {
                            TryAddPhoneme(phonemes, ending.tone, ccr);
                        }   
                    }
                }
            }
            return phonemes;
        }

        protected override string ValidateAlias(string alias) {
            foreach (var VV in new[] { "a 6", "a6" }) {
                alias = alias.Replace(VV, "a a");
            }
            foreach (var CC in new[] { " s" }) {
                alias = alias.Replace(CC, " ts");
            }
            foreach (var CC in new[] { "s" }) {
                alias = alias.Replace(CC, "ts");
            }
            foreach (var CC in new[] { "n S" }) {
                alias = alias.Replace(CC, "n tS");
            }
            foreach (var CC in new[] { "l S" }) {
                alias = alias.Replace(CC, "l tS");
            }
            foreach (var CC in new[] { "nS" }) {
                alias = alias.Replace(CC, "ntS");
            }
            foreach (var CC in new[] { "lS" }) {
                alias = alias.Replace(CC, "ltS");
            }
            return alias;
        }

        protected override double GetTransitionBasicLengthMs(string alias = "") {
            foreach (var c in longConsonants) {
                foreach (var v in vowels) {
                    foreach (var cc in consonants) {
                        if (alias.Contains(" " + c) || alias.Contains(v + c) || alias.Contains(cc + c)) {
                            return base.GetTransitionBasicLengthMs() * 2.0;
                        }
                    }
                }
                
            }
            return base.GetTransitionBasicLengthMs();
        }
    }
}