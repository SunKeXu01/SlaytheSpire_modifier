#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <jni.h>
#include <algorithm>
#include <cstdio>
#include <cstring>

typedef jint (JNICALL *GetCreatedJavaVMsFn)(JavaVM **, jsize, jsize *);

static jclass loadClass(JNIEnv *env, const char *name) {
    jclass loaderClass = env->FindClass("java/lang/ClassLoader");
    if (!loaderClass) { env->ExceptionClear(); return NULL; }
    jmethodID getSystem = env->GetStaticMethodID(loaderClass, "getSystemClassLoader", "()Ljava/lang/ClassLoader;");
    jmethodID load = env->GetMethodID(loaderClass, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
    if (!getSystem || !load) { env->ExceptionClear(); return NULL; }
    jobject loader = env->CallStaticObjectMethod(loaderClass, getSystem);
    jstring className = env->NewStringUTF(name);
    jobject result = env->CallObjectMethod(loader, load, className);
    env->DeleteLocalRef(className);
    env->DeleteLocalRef(loader);
    env->DeleteLocalRef(loaderClass);
    if (env->ExceptionCheck()) { env->ExceptionClear(); return NULL; }
    return (jclass)result;
}

static bool getPlayer(JNIEnv *env, jobject &player, jclass &playerClass, char *error, size_t errorSize) {
    jclass dungeon = loadClass(env, "com.megacrit.cardcrawl.dungeons.AbstractDungeon");
    if (!dungeon) { strncpy_s(error, errorSize, "NOT_READY", _TRUNCATE); return false; }
    jfieldID playerField = env->GetStaticFieldID(dungeon, "player", "Lcom/megacrit/cardcrawl/characters/AbstractPlayer;");
    if (!playerField) { env->ExceptionClear(); env->DeleteLocalRef(dungeon); strncpy_s(error, errorSize, "PLAYER_FIELD", _TRUNCATE); return false; }
    player = env->GetStaticObjectField(dungeon, playerField);
    env->DeleteLocalRef(dungeon);
    if (!player) { strncpy_s(error, errorSize, "NO_RUN", _TRUNCATE); return false; }
    playerClass = env->GetObjectClass(player);
    return playerClass != NULL;
}

static void handleCommand(JNIEnv *env, const char *command, char *response, size_t responseSize) {
    if (strcmp(command, "PING") == 0) {
        strncpy_s(response, responseSize, "OK CONNECTED", _TRUNCATE);
        return;
    }

    if (strcmp(command, "CHECK") == 0) {
        jclass dungeon = loadClass(env, "com.megacrit.cardcrawl.dungeons.AbstractDungeon");
        jclass playerType = loadClass(env, "com.megacrit.cardcrawl.characters.AbstractPlayer");
        jclass energyPanel = loadClass(env, "com.megacrit.cardcrawl.ui.panels.EnergyPanel");
        bool ok = dungeon && playerType && energyPanel &&
            env->GetStaticFieldID(dungeon, "player", "Lcom/megacrit/cardcrawl/characters/AbstractPlayer;") &&
            env->GetFieldID(playerType, "currentHealth", "I") &&
            env->GetFieldID(playerType, "maxHealth", "I") &&
            env->GetStaticFieldID(energyPanel, "totalCount", "I");
        if (env->ExceptionCheck()) env->ExceptionClear();
        if (dungeon) env->DeleteLocalRef(dungeon);
        if (playerType) env->DeleteLocalRef(playerType);
        if (energyPanel) env->DeleteLocalRef(energyPanel);
        strncpy_s(response, responseSize, ok ? "OK COMPATIBLE" : "ERR INCOMPATIBLE", _TRUNCATE);
        return;
    }

    jobject player = NULL;
    jclass playerClass = NULL;
    char error[128] = {0};
    if (!getPlayer(env, player, playerClass, error, sizeof(error))) {
        _snprintf_s(response, responseSize, _TRUNCATE, "ERR %s", error);
        return;
    }

    if (strncmp(command, "ENERGY ", 7) == 0) {
        int amount = atoi(command + 7);
        jclass energyPanel = loadClass(env, "com.megacrit.cardcrawl.ui.panels.EnergyPanel");
        jfieldID currentField = energyPanel ? env->GetStaticFieldID(energyPanel, "totalCount", "I") : NULL;
        if (!energyPanel || !currentField) {
            env->ExceptionClear();
            strncpy_s(response, responseSize, "ERR ENERGY_FIELD", _TRUNCATE);
        } else {
            int value = env->GetStaticIntField(energyPanel, currentField);
            int next = std::max(0, std::min(999, value + amount));
            env->SetStaticIntField(energyPanel, currentField, next);
            _snprintf_s(response, responseSize, _TRUNCATE, "OK ENERGY %d", next);
        }
        if (energyPanel) env->DeleteLocalRef(energyPanel);
    } else if (strncmp(command, "HEAL ", 5) == 0 || strcmp(command, "FULL") == 0) {
        jfieldID currentField = env->GetFieldID(playerClass, "currentHealth", "I");
        jfieldID maxField = env->GetFieldID(playerClass, "maxHealth", "I");
        if (!currentField || !maxField) {
            env->ExceptionClear();
            strncpy_s(response, responseSize, "ERR HEALTH_FIELD", _TRUNCATE);
        } else {
            int current = env->GetIntField(player, currentField);
            int maximum = env->GetIntField(player, maxField);
            int next = strcmp(command, "FULL") == 0 ? maximum : std::min(maximum, current + std::max(0, atoi(command + 5)));
            env->SetIntField(player, currentField, next);
            _snprintf_s(response, responseSize, _TRUNCATE, "OK HEALTH %d %d", next, maximum);
        }
    } else {
        strncpy_s(response, responseSize, "ERR UNKNOWN", _TRUNCATE);
    }

    env->DeleteLocalRef(playerClass);
    env->DeleteLocalRef(player);
}

static DWORD WINAPI bridgeThread(LPVOID) {
    HMODULE jvmModule = NULL;
    for (int i = 0; i < 300 && !jvmModule; ++i) {
        jvmModule = GetModuleHandleW(L"jvm.dll");
        if (!jvmModule) Sleep(100);
    }
    if (!jvmModule) return 1;

    GetCreatedJavaVMsFn getVMs = (GetCreatedJavaVMsFn)GetProcAddress(jvmModule, "JNI_GetCreatedJavaVMs");
    if (!getVMs) return 2;
    JavaVM *vm = NULL;
    jsize count = 0;
    if (getVMs(&vm, 1, &count) != JNI_OK || !vm || count == 0) return 3;
    JNIEnv *env = NULL;
    if (vm->AttachCurrentThread((void **)&env, NULL) != JNI_OK || !env) return 4;

    for (;;) {
        HANDLE pipe = CreateNamedPipeW(L"\\\\.\\pipe\\STSAssistantBridge_v1_0_1",
            PIPE_ACCESS_DUPLEX, PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1, 512, 512, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE) break;
        BOOL connected = ConnectNamedPipe(pipe, NULL) ? TRUE : GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected) {
            char command[128] = {0};
            DWORD read = 0;
            if (ReadFile(pipe, command, sizeof(command) - 1, &read, NULL)) {
                command[read] = 0;
                char response[256] = {0};
                handleCommand(env, command, response, sizeof(response));
                DWORD written = 0;
                WriteFile(pipe, response, (DWORD)strlen(response), &written, NULL);
                FlushFileBuffers(pipe);
            }
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
    vm->DetachCurrentThread();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        HANDLE thread = CreateThread(NULL, 0, bridgeThread, NULL, 0, NULL);
        if (thread) CloseHandle(thread);
    }
    return TRUE;
}
