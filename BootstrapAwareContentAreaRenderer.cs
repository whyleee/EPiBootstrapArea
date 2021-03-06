﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Mvc;
using EPiServer;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using EPiServer.Web.Mvc.Html;
using HtmlAgilityPack;

namespace EPiBootstrapArea
{
    public class BootstrapAwareContentAreaRenderer : ContentAreaRenderer
    {
        private static bool _fallbackCached;
        private static IEnumerable<DisplayModeFallback> _fallbacks;
        private Action<HtmlNode, ContentAreaItem, IContent> _elementStartTagRenderCallback;

        public BootstrapAwareContentAreaRenderer()
        {
            ReadRegisteredDisplayModes();
        }

        public bool RowSupportEnabled { get; set; }

        public bool AutoAddRow { get; set; }

        protected void SetElementStartTagRenderCallback(Action<HtmlNode, ContentAreaItem, IContent> callback)
        {
            _elementStartTagRenderCallback = callback;
        }

        protected override string GetContentAreaItemCssClass(HtmlHelper htmlHelper, ContentAreaItem contentAreaItem)
        {
            var tag = GetContentAreaItemTemplateTag(htmlHelper, contentAreaItem);
            var baseClasses = base.GetContentAreaItemCssClass(htmlHelper, contentAreaItem);

            return string.Format("block {0} {1} {2} {3}",
                                 GetTypeSpecificCssClasses(contentAreaItem, ContentRepository),
                                 GetCssClassesForTag(tag),
                                 tag,
                                 baseClasses);
        }

        public override void Render(HtmlHelper htmlHelper, ContentArea contentArea)
        {
            if (contentArea == null || contentArea.IsEmpty)
            {
                return;
            }

            var viewContext = htmlHelper.ViewContext;
            TagBuilder tagBuilder = null;

            if (!IsInEditMode(htmlHelper) && ShouldRenderWrappingElement(htmlHelper))
            {
                tagBuilder = new TagBuilder(GetContentAreaHtmlTag(htmlHelper, contentArea));
                AddNonEmptyCssClass(tagBuilder, viewContext.ViewData["cssclass"] as string);

                if (AutoAddRow)
                {
                    AddNonEmptyCssClass(tagBuilder, "row");
                }

                viewContext.Writer.Write(tagBuilder.ToString(TagRenderMode.StartTag));
            }

            RenderContentAreaItems(htmlHelper, contentArea.FilteredItems);

            if (tagBuilder == null)
            {
                return;
            }

            viewContext.Writer.Write(tagBuilder.ToString(TagRenderMode.EndTag));
        }

        protected override void RenderContentAreaItems(HtmlHelper htmlHelper, IEnumerable<ContentAreaItem> contentAreaItems)
        {
            var isRowSupported = GetFlagValueFromViewData(htmlHelper, "rowsupport");
            var addRowMarkup = (!isRowSupported.HasValue && RowSupportEnabled) || (isRowSupported ?? false);

            // there is no need to proceed if row rendering support is disabled
            if (!addRowMarkup)
            {
                base.RenderContentAreaItems(htmlHelper, contentAreaItems);
                return;
            }

            var items = contentAreaItems.ToList();
            var rowWidthState = 0;
            var itemInfos = items.Select(item =>
                                         {
                                             var tag = GetContentAreaItemTemplateTag(htmlHelper, item);
                                             var columnWidth = GetColumnWidth(tag);
                                             rowWidthState += columnWidth;
                                             return new
                                             {
                                                 ContentAreaItem = item,
                                                 Tag = tag,
                                                 ColumnWidth = columnWidth,
                                                 RowWidthState = rowWidthState,
                                                 RowNumber = rowWidthState % 12 == 0 ? rowWidthState / 12 - 1 : rowWidthState / 12
                                             };
                                         }).ToList();

            // if tags exists wrap items with row or not then use the default rendering
            var tagExists = itemInfos.Any(ii => !string.IsNullOrEmpty(ii.Tag));
            if (!tagExists)
            {
                base.RenderContentAreaItems(htmlHelper, items);
                return;
            }

            var rows = itemInfos.GroupBy(a => a.RowNumber, a => a.ContentAreaItem);
            foreach (var row in rows)
            {
                htmlHelper.ViewContext.Writer.Write("<div class=\"row row" + row.Key + "\">");
                base.RenderContentAreaItems(htmlHelper, row);
                htmlHelper.ViewContext.Writer.Write("</div>");
            }
        }

        protected override void RenderContentAreaItem(
            HtmlHelper htmlHelper,
            ContentAreaItem contentAreaItem,
            string templateTag,
            string htmlTag,
            string cssClass)
        {
            var originalWriter = htmlHelper.ViewContext.Writer;
            var tempWriter = new StringWriter();
            htmlHelper.ViewContext.Writer = tempWriter;

            try
            {
                var content = contentAreaItem.GetContent(ContentRepository);
                base.RenderContentAreaItem(htmlHelper, contentAreaItem, templateTag, htmlTag, cssClass);
                var contentItemContent = tempWriter.ToString();

                if (IsInEditMode(htmlHelper))
                {
                    // we need to render block if we are in Edit mode
                    originalWriter.Write(contentItemContent);
                }
                else
                {
                    var doc = new HtmlDocument();
                    doc.Load(new StringReader(contentItemContent));
                    var blockContentNode = doc.DocumentNode.ChildNodes.FirstOrDefault();

                    if (blockContentNode == null)
                    {
                        return;
                    }

                    // pass node to callback for some fancy modifications (if any)
                    _elementStartTagRenderCallback?.Invoke(blockContentNode, contentAreaItem, content);

                    if (!string.IsNullOrEmpty(blockContentNode.InnerHtml.Trim(null)))
                    {
                        var renderItemContainer = GetFlagValueFromViewData(htmlHelper, "hasitemcontainer");

                        if (!renderItemContainer.HasValue || renderItemContainer.Value)
                        {
                            originalWriter.Write(blockContentNode.OuterHtml);
                        }
                        else
                        {
                            originalWriter.Write(blockContentNode.InnerHtml);
                        }
                    }
                    else
                    {
                        // ReSharper disable once SuspiciousTypeConversion.Global
                        var visibilityControlledContent = content as IControlVisibility;
                        if ((visibilityControlledContent == null) || !visibilityControlledContent.HideIfEmpty)
                        {
                            originalWriter.Write(blockContentNode.OuterHtml);
                        }
                    }
                }
            }
            finally
            {
                // restore original writer to proceed further with rendering pipeline
                htmlHelper.ViewContext.Writer = originalWriter;
            }
        }

        private static bool? GetFlagValueFromViewData(HtmlHelper htmlHelper, string key)
        {
            var actualValue = htmlHelper.ViewContext.ViewData[key];
            bool? result = null;

            if (actualValue is bool)
            {
                result = (bool) actualValue;
            }

            return result;
        }

        private static int GetColumnWidth(string tag)
        {
            var fallback = _fallbacks.FirstOrDefault(f => f.Tag == tag);
            if (fallback == null)
            {
                return 12;
            }

            return fallback.LargeScreenWidth;
        }

        private static string GetCssClassesForTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
            {
                tagName = ContentAreaTags.FullWidth;
            }

            var fallback = _fallbacks.FirstOrDefault(f => f.Tag == tagName);
            if (fallback == null)
            {
                return string.Empty;
            }

            return string.Format("col-lg-{0} col-md-{1} col-sm-{2} col-xs-{3}",
                                 fallback.LargeScreenWidth,
                                 fallback.MediumScreenWidth,
                                 fallback.SmallScreenWidth,
                                 fallback.ExtraSmallScreenWidth);
        }

        private static string GetTypeSpecificCssClasses(ContentAreaItem contentAreaItem, IContentLoader contentLoader)
        {
            var content = contentAreaItem.GetContent(contentLoader);
            var cssClass = content == null ? string.Empty : content.GetOriginalType().Name.ToLowerInvariant();

            // ReSharper disable once SuspiciousTypeConversion.Global
            var customClassContent = content as ICustomCssInContentArea;
            if (customClassContent != null && !string.IsNullOrWhiteSpace(customClassContent.ContentAreaCssClass))
            {
                cssClass += string.Format(" {0}", customClassContent.ContentAreaCssClass);
            }

            return cssClass;
        }

        private static void ReadRegisteredDisplayModes()
        {
            if (_fallbackCached)
            {
                return;
            }

            var displayModeFallbackProvider = ServiceLocator.Current.GetInstance<IDisplayModeFallbackProvider>();
            _fallbacks = displayModeFallbackProvider.GetAll();
            _fallbackCached = true;
        }
    }
}
