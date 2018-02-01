﻿using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Utilities;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Threading;
using ICommandHandlerAndMetadata = System.Lazy<Microsoft.VisualStudio.Commanding.ICommandHandler, Microsoft.VisualStudio.UI.Text.Commanding.Implementation.ICommandHandlerMetadata>;

namespace Microsoft.VisualStudio.UI.Text.Commanding.Implementation
{
    internal class EditorCommandHandlerService : IEditorCommandHandlerService
    {
        private readonly IEnumerable<ICommandHandlerAndMetadata> _commandHandlers;
        private readonly IUIThreadOperationExecutor _uiThreadOperationExecutor;
        private readonly JoinableTaskContext _joinableTaskContext;
        private readonly ITextView _textView;
        private readonly IComparer<IEnumerable<string>> _contentTypesComparer;
        private readonly ICommandingTextBufferResolver _bufferResolver;
        private readonly IGuardedOperations _guardedOperations;

        private readonly static IReadOnlyList<ICommandHandlerAndMetadata> EmptyHandlerList = new List<ICommandHandlerAndMetadata>(0);
        private readonly static Action EmptyAction = delegate { };
        private readonly static Func<CommandState> UnavalableCommandFunc = new Func<CommandState>(() => CommandState.Unavailable);

        /// This dictionary acts as a cache so we can avoid having to look through the full list of
        /// handlers every time we need handlers of a specific type, for a given content type.
        private readonly Dictionary<(Type commandArgType, IContentType contentType), IReadOnlyList<ICommandHandlerAndMetadata>> _commandHandlersByTypeAndContentType;

        public EditorCommandHandlerService(ITextView textView,
            IEnumerable<ICommandHandlerAndMetadata> commandHandlers,
            IUIThreadOperationExecutor uiThreadOperationExecutor, JoinableTaskContext joinableTaskContext,
            IComparer<IEnumerable<string>> contentTypesComparer,
            ICommandingTextBufferResolver bufferResolver,
            IGuardedOperations guardedOperations)
        {
            _commandHandlers = commandHandlers ?? throw new ArgumentNullException(nameof(commandHandlers));
            _textView = textView ?? throw new ArgumentNullException(nameof(textView));
            _uiThreadOperationExecutor = uiThreadOperationExecutor ?? throw new ArgumentNullException(nameof(uiThreadOperationExecutor));
            _joinableTaskContext = joinableTaskContext ?? throw new ArgumentNullException(nameof(joinableTaskContext));
            _contentTypesComparer = contentTypesComparer ?? throw new ArgumentNullException(nameof(contentTypesComparer));
            _commandHandlersByTypeAndContentType = new Dictionary<(Type commandArgType, IContentType contentType), IReadOnlyList<ICommandHandlerAndMetadata>>();
            _bufferResolver = bufferResolver ?? throw new ArgumentNullException(nameof(bufferResolver));
            _guardedOperations = guardedOperations ?? throw new ArgumentNullException(nameof(guardedOperations));
        }

        public CommandState GetCommandState<T>(Func<ITextView, ITextBuffer, T> argsFactory, Func<CommandState> nextCommandHandler) where T : EditorCommandArgs
        {
            if (!_joinableTaskContext.IsOnMainThread)
            {
                throw new InvalidOperationException($"{nameof(IEditorCommandHandlerService.GetCommandState)} method shoudl only be called on the UI thread.");
            }

            // In Razor scenario it's possible that EditorCommandHandlerService is called re-entrantly,
            // first by contained language command filter and then by editor command chain.
            // To preserve Razor commanding semantics, only execute handlers once.
            if (IsReentrantCall())
            {
                return nextCommandHandler?.Invoke() ?? CommandState.Unavailable;
            }

            using (var reentrancyGuard = new ReentrancyGuard(_textView))
            {
                // Build up chain of handlers per buffer
                Func<CommandState> handlerChain = nextCommandHandler ?? UnavalableCommandFunc;
                foreach (var bufferAndHandler in GetOrderedBuffersAndCommandHandlers<T>().Reverse())
                {
                    T args = null;
                    // Declare locals to ensure that we don't end up capturing the wrong thing
                    var nextHandler = handlerChain;
                    var handler = bufferAndHandler.handler;
                    args = args ?? (args = argsFactory(_textView, bufferAndHandler.buffer));
                    if (args == null)
                    {
                        // Args factory failed, skip command handlers and just call next
                        return handlerChain();
                    }

                    handlerChain = () => handler.GetCommandState(args, nextHandler);
                }

                // Kick off the first command handler
                return handlerChain();
            }
        }

        public void Execute<T>(Func<ITextView, ITextBuffer, T> argsFactory, Action nextCommandHandler) where T : EditorCommandArgs
        {
            if (!_joinableTaskContext.IsOnMainThread)
            {
                throw new InvalidOperationException($"{nameof(IEditorCommandHandlerService.Execute)} method shoudl only be called on the UI thread.");
            }

            // In Razor scenario it's possible that EditorCommandHandlerService is called re-entrantly,
            // first by contained language command filter and then by editor command chain.
            // To preserve Razor commanding semantics, only execute handlers once.
            if (IsReentrantCall())
            {
                nextCommandHandler?.Invoke();
                return;
            }

            using (var reentrancyGuard = new ReentrancyGuard(_textView))
            {
                CommandExecutionContext commandExecutionContext = null;

                // Build up chain of handlers per buffer
                Action handlerChain = nextCommandHandler ?? EmptyAction;
                // TODO: realize the chain dynamically and without Reverse()
                foreach (var bufferAndHandler in GetOrderedBuffersAndCommandHandlers<T>().Reverse())
                {
                    T args = null;
                    // Declare locals to ensure that we don't end up capturing the wrong thing
                    var nextHandler = handlerChain;
                    var handler = bufferAndHandler.handler;
                    args = args ?? (args = argsFactory(_textView, bufferAndHandler.buffer));
                    if (args == null)
                    {
                        // Args factory failed, skip command handlers and just call next
                        handlerChain();
                    }

                    if (commandExecutionContext == null)
                    {
                        commandExecutionContext = CreateCommandExecutionContext();
                    }

                    handlerChain = () => handler.ExecuteCommand(args, nextHandler, commandExecutionContext);
                }

                ExecuteCommandHandlerChain(commandExecutionContext, handlerChain, nextCommandHandler);
            }
        }

        private void ExecuteCommandHandlerChain(CommandExecutionContext commandExecutionContext,
            Action handlerChain, Action nextCommandHandler)
        {
            try
            {
                // Kick off the first command handler.
                handlerChain();
            }
            catch (OperationCanceledException)
            {
                nextCommandHandler?.Invoke();
            }
            catch (AggregateException aggregate) when (aggregate.InnerExceptions.All(e => e is OperationCanceledException))
            {
                nextCommandHandler?.Invoke();
            }
            finally
            {
                commandExecutionContext?.WaitContext?.Dispose();
            }
        }

        private class ReentrancyGuard : IDisposable
        {
            private readonly IPropertyOwner _owner;

            public ReentrancyGuard(IPropertyOwner owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _owner.Properties[typeof(ReentrancyGuard)] = this;
            }

            public void Dispose()
            {
                _owner.Properties.RemoveProperty(typeof(ReentrancyGuard));
            }
        }

        private bool IsReentrantCall()
        {
            return _textView.Properties.ContainsProperty(typeof(ReentrancyGuard));
        }

        private CommandExecutionContext CreateCommandExecutionContext()
        {
            CommandExecutionContext commandExecutionContext;
            var uiThreadOperationContext = _uiThreadOperationExecutor.BeginExecute(CommandingStrings.ExecutingCommand,
                CommandingStrings.WaitForCommandExecution, allowCancel: true, showProgress: true);
            commandExecutionContext = new CommandExecutionContext(uiThreadOperationContext);
            return commandExecutionContext;
        }

        //internal for unit tests
        internal IEnumerable<(ITextBuffer buffer, ICommandHandler handler)> GetOrderedBuffersAndCommandHandlers<T>() where T : EditorCommandArgs
        {
            // This method creates an ordered sequence of (buffer, handler) pairs that define proper order of
            // command handling that takes into account the buffer graph and command handlers matching buffers in the graph by
            // content types.

            // Currently this method discovers affected buffers based on caret mapping only.
            // TODO: this should be an extensibility point as in some scenarios we might want to consider selection too for example.

            // A general idea is that command handlers matching more specifically content type of buffers higher in the buffer
            // graph should be executed before those matching buffers lower in the graph or less specific content types.

            // So for example in a projection scenario (projection buffer containing C# buffer), 3 command handlers
            // matching "projection", "CSharp" and "any" content types will be ordered like this:
            // 1. command handler matching "projection" content type is executed on the projection buffer
            // 2. command handler matching "CSharp" content type is executed on the C# buffer
            // 3. command handler matching "any" content type is executed on the projection buffer

            // The ordering algorithm is as follows:
            // 1. Create an ordered list of all affected buffers in the buffer graph 
            //    by mapping caret position down and up the buffer graph. In a typical projection scenario
            //    (projection buffer containing C# buffer) that will produce (projection buffer, C# buffer) sequence.
            // 2. For each affected buffer get or create a bucket of matching command handlers,
            //    ordered by [Order] and content type specificity.
            // 3. Pick best command handler in all buckets in terms of content type specificity (e.g.
            //    if one command handler can handle "text" content type, but another can
            //    handle "CSharp" content type, we pick the latter one:
            // 3. Start with top command handler in first non empty bucket.
            // 4. Compare it with top command handlers in all other buckets in terms of content type specificity.
            // 5. yield return current handler or better one if found, pop it from its bucket
            // 6. Repeat starting with #3 utill all buckets are empty.
            //    In the projection scenario that will result in the following
            //    list of (buffer, handler) pairs: (projection buffer, projection handler), (C# buffer, C# handler),
            //    (projection buffer, any handler).

            IReadOnlyList<ITextBuffer> buffers = _bufferResolver.ResolveBuffersForCommand<T>().ToArray();
            if (buffers == null || buffers.Count == 0)
            {
                yield break;
            }

            // An array of per-buffer buckets, each containing cached list of matching command handlers,
            // ordered by [Order] and content type specificity
            var handlerBuckets = new CommandHandlerBucket[buffers.Count];
            for (int i = 0; i < buffers.Count; i++)
            {
                handlerBuckets[i] = new CommandHandlerBucket(GetOrCreateOrderedHandlers<T>(buffers[i].ContentType, _textView.Roles));
            }

            while (true)
            {
                ICommandHandlerAndMetadata currentHandler = null;
                int currentHandlerBufferIndex = 0;

                for (int i = 0; i < handlerBuckets.Length; i++)
                {
                    if (!handlerBuckets[i].IsEmpty)
                    {
                        currentHandler = handlerBuckets[i].Peek();
                        currentHandlerBufferIndex = i;
                        break;
                    }
                }

                if (currentHandler == null)
                {
                    // All buckets are empty, all done
                    break;
                }

                // Check if any other bucket has a better handler (i.e. can handle more specific content type).
                var foundBetterHandler = false;
                for (int i = 0; i < buffers.Count; i++)
                {
                    // Search in other buckets only
                    if (i != currentHandlerBufferIndex)
                    {
                        if (!handlerBuckets[i].IsEmpty)
                        {
                            var handler = handlerBuckets[i].Peek();
                            // Can this handler handle content type more specific than top handler in firstNonEmptyBucket?
                            if (_contentTypesComparer.Compare(handler.Metadata.ContentTypes, currentHandler.Metadata.ContentTypes) < 0)
                            {
                                foundBetterHandler = true;
                                handlerBuckets[i].Pop();
                                yield return (buffers[i], handler.Value);
                                break;
                            }
                        }
                    }
                }

                if (!foundBetterHandler)
                {
                    yield return (buffers[currentHandlerBufferIndex], currentHandler.Value);
                    handlerBuckets[currentHandlerBufferIndex].Pop();
                }
            }
        }

        private IReadOnlyList<ICommandHandlerAndMetadata> GetOrCreateOrderedHandlers<T>(IContentType contentType, ITextViewRoleSet textViewRoles) where T : EditorCommandArgs
        {
            var cacheKey = (commandArgsType: typeof(T), contentType: contentType);
            if (!_commandHandlersByTypeAndContentType.TryGetValue(cacheKey, out var commandHandlerList))
            {
                IList<ICommandHandlerAndMetadata> newCommandHandlerList = null;
                foreach (var lazyCommandHandler in SelectMatchingCommandHandlers(_commandHandlers, contentType, textViewRoles))
                {
                    var commandHandler = _guardedOperations.InstantiateExtension<ICommandHandler>(this, lazyCommandHandler);
                    if (commandHandler is ICommandHandler<T> || commandHandler is IChainedCommandHandler<T>)
                    {
                        if (newCommandHandlerList == null)
                        {
                            newCommandHandlerList = new FrugalList<ICommandHandlerAndMetadata>();
                        }

                        newCommandHandlerList.Add(lazyCommandHandler);
                    }
                }

                if (newCommandHandlerList?.Count > 1)
                {
                    // Order handlers by [Order] across content types, but preserve sort order otherwise
                    newCommandHandlerList = StableOrderer.Order(newCommandHandlerList).ToArray();
                }

                commandHandlerList = newCommandHandlerList?.ToArray() ?? EmptyHandlerList;
                _commandHandlersByTypeAndContentType[cacheKey] = commandHandlerList;
            }

            return commandHandlerList;
        }

        /// <summary>
        /// Selects matching command handlers without allocating a new list.
        /// </summary>
        private static IEnumerable<ICommandHandlerAndMetadata> SelectMatchingCommandHandlers(
            IEnumerable<ICommandHandlerAndMetadata> commandHandlers,
            IContentType contentType, ITextViewRoleSet textViewRoles)
        {
            foreach (var handler in commandHandlers)
            {
                if (MatchesContentType(handler.Metadata, contentType) &&
                    MatchesTextViewRoles(handler.Metadata, textViewRoles))
                {
                    yield return handler;
                }
            }
        }

        private static bool MatchesContentType(ICommandHandlerMetadata handlerMetadata, IContentType contentType)
        {
            foreach (var handlerContentType in handlerMetadata.ContentTypes)
            {
                if (contentType.IsOfType(handlerContentType))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesTextViewRoles(ICommandHandlerMetadata handlerMetadata, ITextViewRoleSet roles)
        {
            // Text view roles are optional
            if (handlerMetadata.TextViewRoles == null)
            {
                return true;
            }

            foreach (var handlerRole in handlerMetadata.TextViewRoles)
            {
                if (roles.Contains(handlerRole))
                {
                    return true;
                }
            }

            return false;
        }
    }
}