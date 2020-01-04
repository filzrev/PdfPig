﻿namespace UglyToad.PdfPig.Content
{
    using System;
    using System.Collections.Generic;
    using Exceptions;
    using Graphics.Colors;
    using Parser.Parts;
    using PdfFonts;
    using Tokenization.Scanner;
    using Tokens;
    using Util;

    internal class ResourceStore : IResourceStore
    {
        private readonly IPdfTokenScanner scanner;
        private readonly IFontFactory fontFactory;

        private readonly Dictionary<IndirectReference, IFont> loadedFonts = new Dictionary<IndirectReference, IFont>();
        private readonly StackDictionary<NameToken, IndirectReference> currentResourceState = new StackDictionary<NameToken, IndirectReference>();

        private readonly Dictionary<NameToken, DictionaryToken> extendedGraphicsStates = new Dictionary<NameToken, DictionaryToken>();

        private readonly Dictionary<NameToken, ResourceColorSpace> namedColorSpaces = new Dictionary<NameToken, ResourceColorSpace>();

        private (NameToken name, IFont font) lastLoadedFont;

        public ResourceStore(IPdfTokenScanner scanner, IFontFactory fontFactory)
        {
            this.scanner = scanner;
            this.fontFactory = fontFactory;
        }

        public void LoadResourceDictionary(DictionaryToken resourceDictionary, bool isLenientParsing)
        {
            lastLoadedFont = (null, null);

            currentResourceState.Push();

            if (resourceDictionary.TryGet(NameToken.Font, out var fontBase))
            {
                var fontDictionary = DirectObjectFinder.Get<DictionaryToken>(fontBase, scanner);

                LoadFontDictionary(fontDictionary, isLenientParsing);
            }

            if (resourceDictionary.TryGet(NameToken.Xobject, out var xobjectBase))
            {
                var xobjectDictionary = DirectObjectFinder.Get<DictionaryToken>(xobjectBase, scanner);

                foreach (var pair in xobjectDictionary.Data)
                {
                    if (!(pair.Value is IndirectReferenceToken reference))
                    {
                        throw new InvalidOperationException($"Expected the XObject dictionary value for key /{pair.Key} to be an indirect reference, instead got: {pair.Value}.");
                    }

                    currentResourceState[NameToken.Create(pair.Key)] = reference.Data;
                }
            }

            if (resourceDictionary.TryGet(NameToken.ExtGState, scanner, out DictionaryToken extGStateDictionaryToken))
            {
                foreach (var pair in extGStateDictionaryToken.Data)
                {
                    var name = NameToken.Create(pair.Key);
                    var state = DirectObjectFinder.Get<DictionaryToken>(pair.Value, scanner);

                    extendedGraphicsStates[name] = state;
                }
            }

            if (resourceDictionary.TryGet(NameToken.ColorSpace, scanner, out DictionaryToken colorSpaceDictionary))
            {
                foreach (var nameColorSpacePair in colorSpaceDictionary.Data)
                {
                    var name = NameToken.Create(nameColorSpacePair.Key);

                    if (DirectObjectFinder.TryGet(nameColorSpacePair.Value, scanner, out NameToken colorSpaceName))
                    {
                        namedColorSpaces[name] = new ResourceColorSpace(colorSpaceName);
                    }
                    else if (DirectObjectFinder.TryGet(nameColorSpacePair.Value, scanner, out ArrayToken colorSpaceArray))
                    {
                        if (colorSpaceArray.Length == 0)
                        {
                            throw new PdfDocumentFormatException($"Empty ColorSpace array encountered in page resource dictionary: {resourceDictionary}.");
                        }

                        var first = colorSpaceArray.Data[0];

                        if (!(first is NameToken arrayNamedColorSpace))
                        {
                            throw new PdfDocumentFormatException($"Invalid ColorSpace array encountered in page resource dictionary: {colorSpaceArray}.");
                        }

                        namedColorSpaces[name] = new ResourceColorSpace(arrayNamedColorSpace, colorSpaceArray);
                    }
                    else
                    {
                        throw new PdfDocumentFormatException($"Invalid ColorSpace token encountered in page resource dictionary: {nameColorSpacePair.Value}.");
                    }
                }
            }
        }

        public void UnloadResourceDictionary()
        {
            lastLoadedFont = (null, null);
            currentResourceState.Pop();
        }

        private void LoadFontDictionary(DictionaryToken fontDictionary, bool isLenientParsing)
        {
            lastLoadedFont = (null, null);

            foreach (var pair in fontDictionary.Data)
            {
                if (!(pair.Value is IndirectReferenceToken objectKey))
                {
                    if (isLenientParsing)
                    {
                        continue;
                    }

                    throw new InvalidOperationException($"The font with name {pair.Key} did not link to an object key. Value was: {pair.Value}.");
                }

                var reference = objectKey.Data;

                currentResourceState[NameToken.Create(pair.Key)] = reference;

                if (loadedFonts.ContainsKey(reference))
                {
                    continue;
                }

                var fontObject = DirectObjectFinder.Get<DictionaryToken>(objectKey, scanner);

                if (fontObject == null)
                {
                    throw new InvalidOperationException($"Could not retrieve the font with name: {pair.Key} which should have been object {objectKey}");
                }

                loadedFonts[reference] = fontFactory.Get(fontObject, isLenientParsing);
            }
        }

        public IFont GetFont(NameToken name)
        {
            if (lastLoadedFont.name == name)
            {
                return lastLoadedFont.font;
            }

            var reference = currentResourceState[name];

            loadedFonts.TryGetValue(reference, out var font);

            lastLoadedFont = (name, font);

            return font;
        }

        public IFont GetFontDirectly(IndirectReferenceToken fontReferenceToken, bool isLenientParsing)
        {
            lastLoadedFont = (null, null);

            if (!DirectObjectFinder.TryGet(fontReferenceToken, scanner, out DictionaryToken fontDictionaryToken))
            {
                throw new PdfDocumentFormatException($"The requested font reference token {fontReferenceToken} wasn't a font.");
            }

            var font = fontFactory.Get(fontDictionaryToken, isLenientParsing);

            return font;
        }

        public bool TryGetNamedColorSpace(NameToken name, out ResourceColorSpace namedToken)
        {
            namedToken = default(ResourceColorSpace);

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!namedColorSpaces.TryGetValue(name, out var colorSpaceName))
            {
                return false;
            }

            namedToken = colorSpaceName;

            return true;
        }

        public StreamToken GetXObject(NameToken name)
        {
            var reference = currentResourceState[name];

            var stream = DirectObjectFinder.Get<StreamToken>(new IndirectReferenceToken(reference), scanner);

            return stream;
        }

        public DictionaryToken GetExtendedGraphicsStateDictionary(NameToken name)
        {
            return extendedGraphicsStates[name];
        }
    }
}
