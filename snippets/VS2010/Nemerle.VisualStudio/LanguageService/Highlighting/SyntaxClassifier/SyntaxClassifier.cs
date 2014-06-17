﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Nemerle.Compiler;

namespace Nemerle.VisualStudio.LanguageService
{
  public sealed partial class SyntaxClassifier : IClassifier
  {
    private static readonly HashSet<string> _quotationTypes = new HashSet<string>(StringComparer.InvariantCulture) { "decl", "parameter", "ttype", "case" };

    private readonly IClassificationType[]     _classificationTypes;
    private readonly ITextBuffer               _textBuffer;
    private          ClassificationParseResult _lastParseResult;
    private          IEnumerable<ITextChange>  _lastTextChanges;

    public SyntaxClassifier(
      IStandardClassificationService standardClassification,
      IClassificationTypeRegistryService classificationRegistry,
      ITextBuffer textBuffer)
    {
      _classificationTypes = new IClassificationType[]
      {
        standardClassification.WhiteSpace,
        standardClassification.Identifier,
        standardClassification.Keyword,
        standardClassification.PreprocessorKeyword,
        standardClassification.Operator,
        standardClassification.NumberLiteral,
        standardClassification.CharacterLiteral,
        standardClassification.Comment,
        standardClassification.Comment,
        standardClassification.StringLiteral,
        classificationRegistry.GetClassificationType(ClassificationTypes.VerbatimStringName),
        classificationRegistry.GetClassificationType(ClassificationTypes.RecursiveStringName),
        classificationRegistry.GetClassificationType(ClassificationTypes.QuotationName),
        classificationRegistry.GetClassificationType(ClassificationTypes.QuotationBracesName),
        classificationRegistry.GetClassificationType(ClassificationTypes.ToDoCommentName),
        classificationRegistry.GetClassificationType(ClassificationTypes.BugCommentName),
        classificationRegistry.GetClassificationType(ClassificationTypes.HackCommentName),
      };
      _textBuffer = textBuffer;
      _textBuffer.Changed += TextBuffer_Changed;
    }

    public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
      var parseResult = _lastParseResult;
      if (parseResult == null)
        if (ParseUtil.TryParse(_textBuffer, out parseResult))
          _lastParseResult = parseResult;
        else
          return ClassifierUtils.EmptyClassifications;

      var textChanges = _lastTextChanges;
      if (textChanges != null)
      {
        _lastTextChanges = null;
        var handler = ClassificationChanged;
        if (handler != null)
          foreach (var s in SearchClassificationChanges(parseResult, textChanges.Select(c => c.NewSpan)))
            if (!(span.Span.Start < s.Start && s.End <= span.Span.End))
              handler(this, new ClassificationChangedEventArgs(new SnapshotSpan(parseResult.Snapshot, s)));
      }

      var tokenSpans = GetTokenSpans(_lastParseResult, span.Span);

      var result = new ClassificationSpan[tokenSpans.Count];
      for (var i = 0; i < tokenSpans.Count; ++i)
      {
        var spanInfo = tokenSpans[i];
        result[i] = new ClassificationSpan(new SnapshotSpan(_lastParseResult.Snapshot, spanInfo.Span), _classificationTypes[(int)spanInfo.Type]);
      }
      return result;
    }

    private void TextBuffer_Changed(object sender, TextContentChangedEventArgs e)
    {
      var parseResult = _lastParseResult;
      if (parseResult != null)
      {
        var handler = ClassificationChanged;
        if (handler != null)
          foreach (var s in SearchClassificationChanges(parseResult, e.Changes.Select(c => c.OldSpan)))
            handler(this, new ClassificationChangedEventArgs(new SnapshotSpan(parseResult.Snapshot, s)));
      }
      _lastParseResult = null;
      _lastTextChanges = e.Changes;
    }

    private static List<Span> SearchClassificationChanges(ClassificationParseResult parseResult, IEnumerable<Span> changes)
    {
      var spansToRedraw = new List<Span>();
      foreach (var c in changes)
      {
        var tokenSpans = GetTokenSpans(parseResult, c);
        foreach (var si in tokenSpans)
        {
          switch (si.Type)
          {
            case SpanType.VerbatimString:
            case SpanType.RecursiveString:
            case SpanType.MultiLineComment:
              spansToRedraw.Add(si.Span);
              break;

            case SpanType.PreprocessorKeyword:
              spansToRedraw.Add(new Span(si.Span.Start, parseResult.Snapshot.Length - si.Span.Start));
              break;
          }
        }
      }
      return spansToRedraw;
    }

    private static List<SpanInfo> GetTokenSpans(ClassificationParseResult parseResult, Span span)
    {
      var tokenSpans = new List<SpanInfo>(16);

      foreach (var c in parseResult.Comments)
      {
        if (c.Position > span.End)
          break;

        var commentSpan = new Span(c.Position, c.Length);
        if (span.IntersectsWith(commentSpan))
        {
          InsertClassification(tokenSpans, new SpanInfo(commentSpan, c.IsMultiline ? SpanType.MultiLineComment : SpanType.SingleLineComment));
          if (c.Type != CommentType.Normal)
          {
            var textSpan = new Span(c.TextPosition, c.TextLength);
            SpanType spanType;
            switch (c.Type)
            {
              case CommentType.ToDo: spanType = SpanType.ToDoCommentText; break;
              case CommentType.Bug:  spanType = SpanType.BugCommentText;  break;
              case CommentType.Hack: spanType = SpanType.HackCommentText; break;
              default:               spanType = default(SpanType); Trace.Assert(false); break;
            }
            InsertClassification(tokenSpans, new SpanInfo(textSpan, spanType));
          }
        }
      }

      foreach (var d in parseResult.Directives)
      {
        if (d.Position > span.End)
          break;

        var directiveSpan = new Span(d.Position, d.Length);
        if (span.IntersectsWith(directiveSpan))
          InsertClassification(tokenSpans, new SpanInfo(directiveSpan, SpanType.PreprocessorKeyword));
      }

      List<Span> splices = null; // not used
      WalkTokens(parseResult.Tokens, parseResult.Snapshot, span, tokenSpans, false, ref splices);

      return tokenSpans;
    }

    private static void WalkTokens(Token token, ITextSnapshot textSnapshot, Span span, List<SpanInfo> classifications, bool isQuotation, ref List<Span> splices)
    {
      while (token != null)
      {
        var chunkSpan = GetNextChunkSpan(token, textSnapshot, isQuotation);
        if (chunkSpan.Start > span.End)
          break;

        if (span.IntersectsWith(chunkSpan))
          token = WalkToken(chunkSpan, token, textSnapshot, span, classifications, isQuotation, ref splices);
        else
          token = token.Next;
      }
    }

    private static Span GetNextChunkSpan(Token token, ITextSnapshot textSnapshot, bool isQuotation)
    {
      Token.Operator op2;
      Token body;
      if (isQuotation && (IsSpliceSequence(token, out body) || IsSpliceListSequence(token, out op2, out body)))
      {
        var loc = new Location(token.Location.FileIndex, token.Location.Begin, body.Location.End);
        return Utils.NLocationToSpan(textSnapshot, loc);
      }
      return Utils.NLocationToSpan(textSnapshot, token.Location);
    }

    private static Token WalkToken(Span chunkSpan, Token token, ITextSnapshot textSnapshot, Span span, List<SpanInfo> classifications, bool isQuotation, ref List<Span> splices)
    {
      Token.Operator spliceOp2;
      Token spliceToken;

      if (isQuotation && IsSpliceSequence(token, out spliceToken))
      {
        var spliceOp1Span = Utils.NLocationToSpan(textSnapshot, token.Location);
        if (span.IntersectsWith(spliceOp1Span))
          classifications.Add(new SpanInfo(spliceOp1Span, SpanType.Operator));

        if (splices == null)
          splices = new List<Span>();
        splices.Add(chunkSpan);

        var spliceSpan = Utils.NLocationToSpan(textSnapshot, spliceToken.Location);
        if (span.IntersectsWith(spliceSpan))
        {
          List<Span> innerSplices = null; // not used
          return WalkToken(spliceSpan, spliceToken, textSnapshot, span, classifications, false, ref innerSplices);
        }
        else
          return spliceToken.Next;
      }
      else if (isQuotation && IsSpliceListSequence(token, out spliceOp2, out spliceToken))
      {
        var spliceOp1Span = Utils.NLocationToSpan(textSnapshot, token.Location);
        if (span.IntersectsWith(spliceOp1Span))
          classifications.Add(new SpanInfo(spliceOp1Span, SpanType.Operator));

        var spliceOp2Span = Utils.NLocationToSpan(textSnapshot, spliceOp2.Location);
        if (span.IntersectsWith(spliceOp2Span))
          classifications.Add(new SpanInfo(spliceOp2Span, SpanType.Operator));

        if (splices == null)
          splices = new List<Span>();
        splices.Add(chunkSpan);

        var spliceSpan = Utils.NLocationToSpan(textSnapshot, spliceToken.Location);
        if (span.IntersectsWith(spliceSpan))
        {
          List<Span> innerSplices = null; // not used
          return WalkToken(spliceSpan, spliceToken, textSnapshot, span, classifications, false, ref innerSplices);
        }
        else
          return spliceToken.Next;
      }
      else if (token is Token.Identifier
        || token is Token.IdentifierToComplete
        || token is Token.QuotedIdentifier)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Identifier));
      }
      else if (token is Token.Operator
        || token is Token.Semicolon
        || token is Token.Comma)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Operator));
      }
      else if (token is Token.LooseGroup)
      {
        var group = (Token.LooseGroup)token;
        WalkTokens(group.Child, textSnapshot, span, classifications, isQuotation, ref splices);
      }
      else if (token is Token.RoundGroup)
      {
        var group = (Token.RoundGroup)token;
        if (group.OpenBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.OpenBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        }
        WalkTokens(group.Child, textSnapshot, span, classifications, isQuotation, ref splices);
        if (group.CloseBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.CloseBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        }
      }
      else if (token is Token.SquareGroup)
      {
        var group = (Token.SquareGroup)token;
        if (group.OpenBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.OpenBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        } 
        WalkTokens(group.Child, textSnapshot, span, classifications, isQuotation, ref splices);
        if (group.CloseBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.CloseBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        }
      }
      else if (token is Token.BracesGroup)
      {
        var group = (Token.BracesGroup)token;
        if (group.OpenBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.OpenBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        }
        WalkTokens(group.Child, textSnapshot, span, classifications, isQuotation, ref splices);
        if (group.CloseBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.CloseBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.Operator));
        }
      }
      else if (token is Token.QuoteGroup)
      {
        var group = (Token.QuoteGroup)token;
        if (group.OpenBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.OpenBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.QuotationBrace));
        }

        Token.Identifier type;
        Token.Operator colon;
        Token body;
        if (IsQuotationWithHeader(group, out type, out colon, out body))
        {
          var typeSpan = Utils.NLocationToSpan(textSnapshot, type.Location);
          if (span.IntersectsWith(typeSpan))
            classifications.Add(new SpanInfo(typeSpan, SpanType.Keyword));

          var colonSpan = Utils.NLocationToSpan(textSnapshot, colon.Location);
          if (span.IntersectsWith(colonSpan))
            classifications.Add(new SpanInfo(colonSpan, SpanType.Quotation));
        }
        else
        {
          body = group.Child;
        }

        List<Span> innerSplices = null;
        WalkTokens(body, textSnapshot, span, classifications, true, ref innerSplices);

        if (innerSplices == null)
          InsertClassification(classifications, new SpanInfo(chunkSpan, SpanType.Quotation));
        else
        {
          var pos = chunkSpan.Start;
          foreach (var s in innerSplices)
          {
            InsertClassification(classifications, new SpanInfo(new Span(pos, s.Start - pos), SpanType.Quotation));
            pos = s.End;
          }
          InsertClassification(classifications, new SpanInfo(new Span(pos, chunkSpan.End - pos), SpanType.Quotation));
        }

        if (group.CloseBrace != null)
        {
          var braceSpan = Utils.NLocationToSpan(textSnapshot, group.CloseBrace.Location);
          if (span.IntersectsWith(braceSpan))
            classifications.Add(new SpanInfo(braceSpan, SpanType.QuotationBrace));
        }
      }
      else if (token is Token.DecimalLiteral
        || token is Token.DoubleLiteral
        || token is Token.FloatLiteral
        || token is Token.IntegerLiteral)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Number));
      }
      else if (token is Token.Keyword)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Keyword));
      }
      else if (token is Token.CharLiteral)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Character));
      }
      else if (token is Token.StringLiteral)
      {
        classifications.Add(new SpanInfo(chunkSpan, GetStringType((Token.StringLiteral)token)));
      }
      else if (token is Token.WhiteSpace)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.Whitespace));
      }
      else if (token is Token.Comment)
      {
        classifications.Add(new SpanInfo(chunkSpan, SpanType.MultiLineComment));
      }
      else if (token is Token.Namespace)
      {
        var nsToken = (Token.Namespace)token;
        WalkTokens(nsToken.KeywordToken, textSnapshot, span, classifications, isQuotation, ref splices);
        WalkTokens(nsToken.Body, textSnapshot, span, classifications, isQuotation, ref splices);
      }
      else if (token is Token.Using)
      {
        var nsToken = (Token.Using)token;
        WalkTokens(nsToken.KeywordToken, textSnapshot, span, classifications, isQuotation, ref splices);
        WalkTokens(nsToken.Body, textSnapshot, span, classifications, isQuotation, ref splices);
      }

      return token.Next;
    }

    private static void InsertClassification(List<SpanInfo> items, SpanInfo span)
    {
      var index = items.BinarySearch(span);
      items.Insert(index >= 0 ? index : ~index, span);
    }

    private static SpanType GetStringType(Token.StringLiteral token)
    {
      var c1 = token.rawString[0];
      if (c1 == '"')
        return SpanType.String;

      var c2 = token.rawString[1];
      if (c1 == '@' && c2 == '"')
        return SpanType.VerbatimString;

      if (c1 == '<' && c2 == '#')
        return SpanType.RecursiveString;

      Trace.Assert(false);
      return SpanType.Whitespace;
    }

    private static bool IsSpliceSequence(Token token, out Token body)
    {
      var _op1 = token as Token.Operator;
      if (_op1 != null && string.Equals(_op1.name, "$", StringComparison.InvariantCulture) && _op1.Next != null)
      {
        body = _op1.Next;
        return true;
      }
      body = null;
      return false;
    }

    private static bool IsSpliceListSequence(Token token, out Token.Operator op2, out Token body)
    {
      var _op1 = token as Token.Operator;
      if (_op1 != null && string.Equals(_op1.name, "..", StringComparison.InvariantCulture))
      {
        var _op2 = _op1.Next as Token.Operator;
        if (_op2 != null && string.Equals(_op2.name, "$", StringComparison.InvariantCulture) && _op2.Next != null)
        {
          op2 = _op2;
          body = _op2.Next;
          return true;
        }
      }
      op2 = null;
      body = null;
      return false;
    }

    private static bool IsQuotationWithHeader(Token.QuoteGroup group, out Token.Identifier type, out Token.Operator colon, out Token body)
    {
      var content = group.Child as Token.LooseGroup;
      if (content != null)
      {
        var _type = content.Child as Token.Identifier;
        if (_type != null && _quotationTypes.Contains(_type.name))
        {
          var _colon = _type.Next as Token.Operator;
          if (_colon != null && string.Equals(_colon.name, ":", StringComparison.InvariantCulture))
          {
            type  = _type;
            colon = _colon;
            body  = _colon.Next;
            return true;
          }
        }
      }
      type  = null;
      colon = null;
      body  = null;
      return false;
    }
  }
}
