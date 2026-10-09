// Native window for Tbot Ultra on macOS. The automation engine is the bundled .NET server
// (Contents/Resources/server/TbotUltraMac); this shell starts it, shows its local UI in a
// WKWebView, and stops it (which also closes the automated browser) when the app quits.
#import <Cocoa/Cocoa.h>
#import <WebKit/WebKit.h>
#include <signal.h>

static NSString *const ListeningPrefix = @"TBOT_LISTENING ";

@interface AppDelegate : NSObject <NSApplicationDelegate, WKUIDelegate, WKNavigationDelegate>
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, strong) WKWebView *webView;
@property(nonatomic, strong) NSTask *server;
@property(nonatomic, strong) NSURL *serverURL;
@property(nonatomic, strong) NSMutableString *output;
@property(nonatomic) BOOL quitting;
@end

@implementation AppDelegate

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    self.output = [NSMutableString string];
    [self buildMenu];

    self.webView = [[WKWebView alloc] initWithFrame:NSZeroRect configuration:[[WKWebViewConfiguration alloc] init]];
    self.webView.UIDelegate = self;
    self.webView.navigationDelegate = self;

    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 1180, 780)
                                              styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable
                                                        | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskResizable
                                                backing:NSBackingStoreBuffered
                                                  defer:NO];
    self.window.title = @"Tbot Ultra";
    self.window.minSize = NSMakeSize(860, 560);
    self.window.contentView = self.webView;
    [self.window center];
    [self.window setFrameAutosaveName:@"TbotUltraMainWindow"];
    [self.window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];

    [self.webView loadHTMLString:@"<body style='background:#14161a;color:#8e96a3;font:13px -apple-system;padding:24px'>Starting Tbot Ultra…</body>"
                         baseURL:nil];
    [self startServer];
}

- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender {
    return YES;
}

- (void)applicationWillTerminate:(NSNotification *)notification {
    self.quitting = YES;
    if (!self.server.isRunning) {
        return;
    }

    // SIGTERM lets the server close the automated browser before it exits.
    [self.server terminate];
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:15];
    while (self.server.isRunning && [deadline timeIntervalSinceNow] > 0) {
        [NSThread sleepForTimeInterval:0.1];
    }

    if (self.server.isRunning) {
        kill(self.server.processIdentifier, SIGKILL);
    }
}

- (void)startServer {
    NSURL *serverPath = [[NSBundle mainBundle].resourceURL URLByAppendingPathComponent:@"server/TbotUltraMac"];
    if (![[NSFileManager defaultManager] isExecutableFileAtPath:serverPath.path]) {
        [self fail:[NSString stringWithFormat:@"The bundled engine is missing:\n%@", serverPath.path]];
        return;
    }

    NSTask *task = [[NSTask alloc] init];
    task.executableURL = serverPath;
    task.currentDirectoryURL = [serverPath URLByDeletingLastPathComponent];
    NSPipe *pipe = [NSPipe pipe];
    task.standardOutput = pipe;
    task.standardError = pipe;

    __weak AppDelegate *weakSelf = self;
    pipe.fileHandleForReading.readabilityHandler = ^(NSFileHandle *handle) {
        NSString *text = [[NSString alloc] initWithData:handle.availableData encoding:NSUTF8StringEncoding];
        if (text.length > 0) {
            dispatch_async(dispatch_get_main_queue(), ^{ [weakSelf consume:text]; });
        }
    };
    task.terminationHandler = ^(NSTask *finished) {
        dispatch_async(dispatch_get_main_queue(), ^{
            AppDelegate *strongSelf = weakSelf;
            if (strongSelf == nil || strongSelf.quitting) {
                return;
            }

            NSString *tail = strongSelf.output.length > 600
                ? [strongSelf.output substringFromIndex:strongSelf.output.length - 600]
                : strongSelf.output;
            [strongSelf fail:[NSString stringWithFormat:@"The engine stopped unexpectedly (exit code %d).\n\n%@",
                                                        finished.terminationStatus, tail]];
        });
    };

    NSError *error = nil;
    if (![task launchAndReturnError:&error]) {
        [self fail:[NSString stringWithFormat:@"Could not start the engine: %@", error.localizedDescription]];
        return;
    }

    self.server = task;
}

- (void)consume:(NSString *)text {
    [self.output appendString:text];
    if (self.serverURL != nil) {
        return;
    }

    for (NSString *line in [self.output componentsSeparatedByCharactersInSet:[NSCharacterSet newlineCharacterSet]]) {
        if ([line hasPrefix:ListeningPrefix]) {
            NSString *address = [[line substringFromIndex:ListeningPrefix.length]
                stringByTrimmingCharactersInSet:[NSCharacterSet whitespaceCharacterSet]];
            self.serverURL = [NSURL URLWithString:address];
            [self.webView loadRequest:[NSURLRequest requestWithURL:self.serverURL]];
            return;
        }
    }
}

- (void)fail:(NSString *)message {
    self.quitting = YES;
    NSAlert *alert = [[NSAlert alloc] init];
    alert.alertStyle = NSAlertStyleCritical;
    alert.messageText = @"Tbot Ultra could not start";
    alert.informativeText = message;
    [alert runModal];
    [NSApp terminate:nil];
}

// The UI is local only; anything else opens in the default browser.
- (void)webView:(WKWebView *)webView
    decidePolicyForNavigationAction:(WKNavigationAction *)navigationAction
                    decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    NSURL *url = navigationAction.request.URL;
    if (url.host != nil && ![url.host isEqualToString:@"127.0.0.1"]) {
        [[NSWorkspace sharedWorkspace] openURL:url];
        decisionHandler(WKNavigationActionPolicyCancel);
        return;
    }

    decisionHandler(WKNavigationActionPolicyAllow);
}

- (void)webView:(WKWebView *)webView
    runJavaScriptConfirmPanelWithMessage:(NSString *)message
                        initiatedByFrame:(WKFrameInfo *)frame
                       completionHandler:(void (^)(BOOL))completionHandler {
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = message;
    [alert addButtonWithTitle:@"OK"];
    [alert addButtonWithTitle:@"Cancel"];
    completionHandler([alert runModal] == NSAlertFirstButtonReturn);
}

- (void)webView:(WKWebView *)webView
    runJavaScriptAlertPanelWithMessage:(NSString *)message
                      initiatedByFrame:(WKFrameInfo *)frame
                     completionHandler:(void (^)(void))completionHandler {
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = message;
    [alert runModal];
    completionHandler();
}

- (void)buildMenu {
    NSMenu *mainMenu = [[NSMenu alloc] init];

    NSMenuItem *appItem = [[NSMenuItem alloc] init];
    NSMenu *appMenu = [[NSMenu alloc] init];
    [appMenu addItemWithTitle:@"About Tbot Ultra" action:@selector(orderFrontStandardAboutPanel:) keyEquivalent:@""];
    [appMenu addItem:[NSMenuItem separatorItem]];
    [appMenu addItemWithTitle:@"Hide Tbot Ultra" action:@selector(hide:) keyEquivalent:@"h"];
    [appMenu addItemWithTitle:@"Quit Tbot Ultra" action:@selector(terminate:) keyEquivalent:@"q"];
    appItem.submenu = appMenu;
    [mainMenu addItem:appItem];

    // Without an Edit menu, Cmd+C / Cmd+V do not reach text fields inside the web view.
    NSMenuItem *editItem = [[NSMenuItem alloc] init];
    NSMenu *editMenu = [[NSMenu alloc] initWithTitle:@"Edit"];
    [editMenu addItemWithTitle:@"Undo" action:@selector(undo:) keyEquivalent:@"z"];
    [editMenu addItemWithTitle:@"Redo" action:@selector(redo:) keyEquivalent:@"Z"];
    [editMenu addItem:[NSMenuItem separatorItem]];
    [editMenu addItemWithTitle:@"Cut" action:@selector(cut:) keyEquivalent:@"x"];
    [editMenu addItemWithTitle:@"Copy" action:@selector(copy:) keyEquivalent:@"c"];
    [editMenu addItemWithTitle:@"Paste" action:@selector(paste:) keyEquivalent:@"v"];
    [editMenu addItemWithTitle:@"Select All" action:@selector(selectAll:) keyEquivalent:@"a"];
    editItem.submenu = editMenu;
    [mainMenu addItem:editItem];

    NSMenuItem *windowItem = [[NSMenuItem alloc] init];
    NSMenu *windowMenu = [[NSMenu alloc] initWithTitle:@"Window"];
    [windowMenu addItemWithTitle:@"Minimize" action:@selector(performMiniaturize:) keyEquivalent:@"m"];
    [windowMenu addItemWithTitle:@"Close" action:@selector(performClose:) keyEquivalent:@"w"];
    windowItem.submenu = windowMenu;
    [mainMenu addItem:windowItem];

    NSApp.mainMenu = mainMenu;
    NSApp.windowsMenu = windowMenu;
}

@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSApplication *app = [NSApplication sharedApplication];
        AppDelegate *delegate = [[AppDelegate alloc] init];
        app.delegate = delegate;
        [app setActivationPolicy:NSApplicationActivationPolicyRegular];
        [app run];
    }

    return 0;
}
