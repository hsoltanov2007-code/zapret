# Native engine extension point

No native engine is implemented or registered in v0.2. Implement `IDpiEngine`,
then register its factory in `App.xaml.cs`. Keep C++/PInvoke details inside this
assembly; the view models and profile store depend only on the engine API.
Do not register a placeholder that claims to start or protect traffic.
